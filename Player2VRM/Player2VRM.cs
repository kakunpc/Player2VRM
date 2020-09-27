﻿using HarmonyLib;
using Oc;
using SR;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using UniGLTF;
using UniRx;
using UnityEngine;
using VRM;
using Oc.Item;

namespace Player2VRM
{
    [HarmonyPatch(typeof(OcPlHeadPrefabSetting))]
    [HarmonyPatch("Start")]
    static class OcPlHeadPrefabSettingVRM
    {
        static void Postfix(OcPlHeadPrefabSetting __instance)
        {
            var slave = __instance.GetComponentInParentRecursive<OcPlSlave>();
            if (slave && !slave.FindNameInParentRecursive("UI"))
            {
                var selfId = OcNetMng.Inst.NetPlId_Master;
                if (SingletonMonoBehaviour<OcPlMng>.Inst.getPlSlave(selfId - 1) != slave) return;
            }

            foreach (var mr in __instance.GetComponentsInChildren<MeshRenderer>())
            {
                mr.enabled = false;
            }
        }
    }


    [HarmonyPatch(typeof(OcPl))]
    [HarmonyPatch(nameof(OcPl.lateMove))]
    static class EquipAdjustPos_OcPlEquipCtrl_lateMove
    {
        // OcPlEquipCtrlに紐づくOcPlEquipのリスト
        static readonly Dictionary<OcPlEquipCtrl, HashSet<OcPlEquip>> plEquipCtrlCorrespondedplEquips = new Dictionary<OcPlEquipCtrl, HashSet<OcPlEquip>>();

        // OcEquipSlot別の、VRMモデルのどのボーンを親にするかの設定
        static readonly IReadOnlyDictionary<OcEquipSlot, HumanBodyBones> epuipBaseBones = new Dictionary<OcEquipSlot, HumanBodyBones>
        {
            {OcEquipSlot.EqHead, HumanBodyBones.Head},
            {OcEquipSlot.Accessory, HumanBodyBones.Hips},
            {OcEquipSlot.FlightUnit, HumanBodyBones.Hips},
            {OcEquipSlot.WpSub, HumanBodyBones.RightHand},
            {OcEquipSlot.WpDual, HumanBodyBones.Spine},
            {OcEquipSlot.WpTwoHand, HumanBodyBones.Spine},
            {OcEquipSlot.Ammo, HumanBodyBones.Hips},
            {OcEquipSlot.EqBody, HumanBodyBones.Hips},
            {OcEquipSlot.WpMain, HumanBodyBones.Spine},
        };

        // OcEquipSlot別の設定ファイルのKey名
        static readonly IReadOnlyDictionary<OcEquipSlot, string> equipSlot2Key = new Dictionary<OcEquipSlot, string>
        {
            { OcEquipSlot.EqHead, "EquipHead" },
            { OcEquipSlot.Accessory, "EquipAccessory" },
            { OcEquipSlot.FlightUnit, "EquipFlightUnit" },
            { OcEquipSlot.WpSub, "EquipSub" },
            { OcEquipSlot.WpDual, "EquipDual" },
            { OcEquipSlot.WpTwoHand, "EquipTwoHand" },
            { OcEquipSlot.Ammo, "EquipAmmo" }, // これの位置を変えると何に影響があるのか不明
            { OcEquipSlot.EqBody, "EquipBody" }, // これの位置を変えると何に影響があるのか不明
            { OcEquipSlot.WpMain, "EquipMain" }, // これの位置を変えると、ピッケル・斧の所持位置と、壁などの設置場所（！！）が変わる。
        };

        // 装備位置変更設定をキャッシュするか（毎フレームパースするのは無駄）
        static bool? cachingEnabled = null;
        static bool CachingEnabled
        {
            get
            {
                if (cachingEnabled.HasValue && cachingEnabled.Value == true) return true;
                cachingEnabled = !Settings.ReadBool("DynamicEquipAdjustment", false);
                return cachingEnabled.Value;
            }
        }

        // 装備位置変更設定のキャッシュ（VRMモデルに合わせるかどうか、オフセット値）
        static readonly Dictionary<OcEquipSlot, bool> equipPositionIsAdujstedToVrmModel = new Dictionary<OcEquipSlot, bool>();
        static readonly Dictionary<OcEquipSlot, Vector3> equipPositionOffsets = new Dictionary<OcEquipSlot, Vector3>();
        // 装備の本来の親Transform
        static readonly Dictionary<OcPlEquipCtrl, Transform> originalParentTransform = new Dictionary<OcPlEquipCtrl, Transform>();

        // VRMモデルのアニメータのキャッシュ（念の為OcPl別にキャッシュ）
        static readonly Dictionary<OcPl, Animator> plRelatedModelAnimator = new Dictionary<OcPl, Animator>();

        // 装備品一覧の取得
        internal static HashSet<OcPlEquip> GetPlEquips(OcPlEquipCtrl plEquipCtrl)
        {
            if(plEquipCtrlCorrespondedplEquips.TryGetValue(plEquipCtrl, out var plEquips))
            {
                return plEquips;
            }
            else
            {
                // OcPlEquipCtrlがDestoryされるタイミングがわからないので、新規追加のタイミングでDictionary中のOcPlEquipCtrlの存在チェックを実施する
                foreach(var destroyedPlEquipCtrl in plEquipCtrlCorrespondedplEquips.Keys.Where(key => key == null).ToArray())
                {
                   plEquipCtrlCorrespondedplEquips.Remove(destroyedPlEquipCtrl);
                }
                foreach (var destroyedPlEquipCtrl in originalParentTransform.Keys.Where(key => key == null).ToArray())
                {
                    originalParentTransform.Remove(destroyedPlEquipCtrl);
                }
                
                var newPlEquips = new HashSet<OcPlEquip>();
                plEquipCtrlCorrespondedplEquips.Add(plEquipCtrl, newPlEquips);
                return newPlEquips;
            }
        }

        static Vector3 GetOffset(OcEquipSlot equipSlot)
        {
            Vector3 offset;
            if(equipPositionOffsets.TryGetValue(equipSlot, out offset) && CachingEnabled)
            {
                return offset;
            }

            offset = Settings.ReadVector3($"{equipSlot2Key[equipSlot]}Offset", Vector3.zero);
            if (CachingEnabled) equipPositionOffsets.Add(equipSlot, offset);
            return offset;

        }
        static bool IsAdujstedToVrmModel(OcEquipSlot equipSlot)
        {
            bool result;
            if(equipPositionIsAdujstedToVrmModel.TryGetValue(equipSlot, out result) && CachingEnabled)
            {
                return result;
            }

            result = Settings.ReadBool($"{equipSlot2Key[equipSlot]}FollowsModel", false);
            if(CachingEnabled) equipPositionIsAdujstedToVrmModel.Add(equipSlot, result);
            return result;
        }

        static Animator GetPlRelatedModelAnimator(OcPl pl)
        {
            if (plRelatedModelAnimator.TryGetValue(pl, out var anim) == false || anim == null) // Dictionaryにキャッシュされて無いorデストロイ済み
            {
                anim = pl
                    .Animator.gameObject
                    .GetComponent<CloneHumanoid>()
                    .GetVrmModel()
                    .GetComponent<Animator>();
                plRelatedModelAnimator[pl] = anim; // インデクサでのアクセスならkeyの存在有無にかかわらず追加・更新できる
            }
            return anim;
        }

        static void AdjustEquipPos(OcPlEquip plEquip)
        {
            if (IsAdujstedToVrmModel(plEquip.EquipSlot) && epuipBaseBones.TryGetValue(plEquip.EquipSlot, out var bone))
            {
                var modelHeadTrans = GetPlRelatedModelAnimator(plEquip.OwnerPl).GetBoneTransform(bone);
                plEquip.transform.SetParent(modelHeadTrans, false);
                plEquip.SetLocalPosition(GetOffset(plEquip.EquipSlot));
                return;
            }
            else
            {
                plEquip.TransSelf.SetParent(originalParentTransform[plEquip.OwnerPl.EquipCtrl], true);
                plEquip.TransSelf.localPosition += GetOffset(plEquip.EquipSlot);
            }
        }

        // 矢筒は他の装備品と管理方法が違うので別途対応（やってることはほぼ同じ）
        static readonly Dictionary<OcPlCommon, Transform> quiverTransforms = new Dictionary<OcPlCommon, Transform>();
        static Vector3? quiverOffset = null;
        static bool? isQuiverAdujstedToVrmModel = null;

        static Vector3 GetQuiverOffset()
        {
            if (quiverOffset.HasValue && EquipAdjustPos_OcPlEquipCtrl_lateMove.CachingEnabled) return quiverOffset.Value;
            quiverOffset = Settings.ReadVector3("EquipArrowOffset", Vector3.zero);
            return quiverOffset.Value;
        }

        static bool IsQuiverAdujstedToVrmModel()
        {
            if (isQuiverAdujstedToVrmModel.HasValue && EquipAdjustPos_OcPlEquipCtrl_lateMove.CachingEnabled) return isQuiverAdujstedToVrmModel.Value;
            isQuiverAdujstedToVrmModel = Settings.ReadBool("EquipArrowFollowsModel", false);
            return isQuiverAdujstedToVrmModel.Value;
        }

        static void AdjustEquipPos(OcPl pl, OcPlCommon plCommon)
        {
            Transform quiver;
            if (quiverTransforms.TryGetValue(plCommon, out quiver) == false || quiver == null)
            {
                quiver = plCommon.AccessoryCtrl.transform.Find("OcQuiver");
                if (quiver != null) quiverTransforms.Add(plCommon, quiver);
            }

            if (quiver == null) return;

            if (IsQuiverAdujstedToVrmModel())
            {
                var modelHeadTrans = GetPlRelatedModelAnimator(pl).GetBoneTransform(HumanBodyBones.Spine);
                quiver.SetParent(modelHeadTrans, false);
                quiver.SetLocalPosition(GetQuiverOffset());
                return;
            }
            else
            {
                quiver.SetParent(plCommon.AccessoryCtrl, true);
                quiver.localPosition += GetQuiverOffset();
            }

        }

        static void Postfix(OcPl __instance)
        {
            var plEquipCtrl = __instance.EquipCtrl;
            var plCommon = __instance.PlCommon;
            var plEquips = GetPlEquips(plEquipCtrl);
            plEquips.RemoveWhere(plEquip => plEquip == null); // Destroyされていたら null チェックが True になる

            if (originalParentTransform.ContainsKey(plEquipCtrl) == false && plEquips.Any())
            {
                originalParentTransform.Add(plEquipCtrl, IEnumerableExtensions.First(plEquips).transform.parent);
            }

            foreach (var plEquip in plEquips)
            {
                AdjustEquipPos(plEquip);
            }

            AdjustEquipPos(__instance, __instance.PlCommon);
        }
    }


    [HarmonyPatch(typeof(OcPlEquipCtrl))]
    [HarmonyPatch(nameof(OcPlEquipCtrl.setEquip))]
    static class EquipAdjustPos_OcPlEquipCtrl_setEquip
    {
        static bool Prefix(OcPlEquipCtrl __instance, OcItem item, OcEquipSlot equipSlot, out OcEquipSlot __state)
        {
            __state = equipSlot;
            return true;
        }

        // 装備変更のタイミングで装備品リストを更新（追加）
        static void Postfix(OcPlEquipCtrl __instance, OcEquipSlot __state)
        {
            EquipAdjustPos_OcPlEquipCtrl_lateMove.GetPlEquips(__instance).Add(__instance.getEquip(__state));
        }

    }


    [HarmonyPatch(typeof(OcPlEquip))]
    [HarmonyPatch("setDraw")]
    static class OcPlEquipVRM
    {
        static bool Prefix(OcPlEquip __instance, ref bool isDraw)
        {
            var slave = __instance.GetComponentInParentRecursive<OcPlSlave>();
            if (slave && !slave.FindNameInParentRecursive("UI"))
            {
                var selfId = OcNetMng.Inst.NetPlId_Master;
                if (SingletonMonoBehaviour<OcPlMng>.Inst.getPlSlave(selfId - 1) != slave) return true;
            }

            if (__instance.EquipSlot == OcEquipSlot.EqHead && !Settings.ReadBool("DrawEquipHead", true))
            {
                isDraw = false;
                return true;
            }

            if (__instance.EquipSlot == OcEquipSlot.Accessory && !Settings.ReadBool("DrawEquipAccessory", true))
            {
                isDraw = false;
                return true;
            }

            if (__instance.EquipSlot == OcEquipSlot.WpSub && !Settings.ReadBool("DrawEquipShield", true))
            {
                isDraw = false;
                return true;
            }

            return true;
        }
    }

    [HarmonyPatch(typeof(OcAccessoryCtrl))]
    [HarmonyPatch("setAf_DrawFlag")]
    static class OcAccessoryCtrlVRM
    {
        static bool Prefix(OcAccessoryCtrl __instance, OcAccessoryCtrl.AccType type)
        {
            if (type == OcAccessoryCtrl.AccType.Quiver)
            {
                return Settings.ReadBool("DrawEquipArrow");
            }
            return true;
        }
    }

    [HarmonyPatch(typeof(OcPlCharacterBuilder))]
    [HarmonyPatch("ChangeHair")]
    static class OcPlCharacterBuilderVRM
    {
        static void Postfix(OcPlCharacterBuilder __instance, GameObject prefab, int? layer = null)
        {
            var go = __instance.GetRefField<OcPlCharacterBuilder, GameObject>("hair");

            var slave = go.GetComponentInParentRecursive<OcPlSlave>();
            if (slave && !slave.FindNameInParentRecursive("UI"))
            {
                var selfId = OcNetMng.Inst.NetPlId_Master;
                if (SingletonMonoBehaviour<OcPlMng>.Inst.getPlSlave(selfId - 1) != slave) return;
            }

            foreach (var mr in go.GetComponentsInChildren<MeshRenderer>())
            {
                mr.enabled = false;
            }
            foreach (var smr in go.GetComponentsInChildren<SkinnedMeshRenderer>())
            {
                smr.enabled = false;
            }
        }
    }

    [HarmonyPatch(typeof(ShaderStore))]
    [HarmonyPatch("GetShader")]
    static class ShaderToRealToon
    {
        static bool Prefix(ShaderStore __instance, ref Shader __result, glTFMaterial material)
        {
            if (Settings.ReadBool("UseRealToonShader", false))
            {
                __result = Shader.Find("RealToon/Version 5/Default/Default");
                return false;
            }

            return true;
        }
    }

    [HarmonyPatch(typeof(MaterialImporter))]
    [HarmonyPatch("CreateMaterial")]
    static class MaterialImporterVRM
    {
        static void Postfix(MaterialImporter __instance, ref Material __result, int i, glTFMaterial x, bool hasVertexColor)
        {
            __result.SetFloat("_DoubleSided", x.doubleSided ? 0 : 2);
            __result.SetFloat("_Cutout", x.alphaCutoff);

            if (x.pbrMetallicRoughness != null)
            {
                if (x.pbrMetallicRoughness.baseColorFactor != null && x.pbrMetallicRoughness.baseColorFactor.Length == 3)
                {
                    float[] baseColorFactor2 = x.pbrMetallicRoughness.baseColorFactor;
                    var max = baseColorFactor2.Max();
                    var rate = Mathf.Min(0.688f / max, 1.0f);
                    __result.SetColor("_MainColor", new Color(baseColorFactor2[0] * rate, baseColorFactor2[1] * rate, baseColorFactor2[2] * rate));
                }
                else if (x.pbrMetallicRoughness.baseColorFactor != null && x.pbrMetallicRoughness.baseColorFactor.Length == 4)
                {
                    float[] baseColorFactor2 = x.pbrMetallicRoughness.baseColorFactor;
                    var facotrs = new float[] { baseColorFactor2[0], baseColorFactor2[1], baseColorFactor2[2] };
                    var max = facotrs.Max();
                    var rate = Mathf.Min(0.688f / max, 1.0f);
                    __result.SetColor("_MainColor", new Color(baseColorFactor2[0] * rate, baseColorFactor2[1] * rate, baseColorFactor2[2] * rate, baseColorFactor2[3]));
                }
            }
        }
    }

    [HarmonyPatch(typeof(Shader))]
    [HarmonyPatch(nameof(Shader.Find))]
    static class ShaderPatch
    {
        static bool Prefix(ref Shader __result, string name)
        {
            if (VRMShaders.Shaders.TryGetValue(name, out var shader))
            {
                __result = shader;
                return false;
            }

            return true;
        }
    }

    public static class VRMShaders
    {
        public static Dictionary<string, Shader> Shaders { get; } = new Dictionary<string, Shader>();

        public static void Initialize()
        {
            var bundlePath = Path.Combine(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location), @"Player2VRM.shaders");
            if (File.Exists(bundlePath))
            {
                var assetBundle = AssetBundle.LoadFromFile(bundlePath);
                var assets = assetBundle.LoadAllAssets<Shader>();
                foreach (var asset in assets)
                {
                    UnityEngine.Debug.Log("Add Shader: " + asset.name);
                    Shaders.Add(asset.name, asset);
                }
            }
        }
    }

    [DefaultExecutionOrder(int.MaxValue - 100)]
    internal class CloneHumanoid : MonoBehaviour
    {
        HumanPoseHandler orgPose, vrmPose;
        HumanPose hp = new HumanPose();
        GameObject instancedModel;
        internal GameObject GetVrmModel() => instancedModel;
        VRMBlendShapeProxy blendProxy;
        Facial.FaceCtrl facialFace;

        public void Setup(GameObject vrmModel, Animator orgAnim, bool isMaster)
        {
            var instance = instancedModel ?? Instantiate(vrmModel);
            var useRealToon = Settings.ReadBool("UseRealToonShader", false);
            foreach (var sm in instance.GetComponentsInChildren<Renderer>())
            {
                sm.enabled = true;
                if (useRealToon)
                {
                    foreach (var mat in sm.materials)
                    {
                        mat.SetFloat("_EnableTextureTransparent", 1.0f);
                    }
                }
            }

            instance.transform.SetParent(orgAnim.transform, false);
            PoseHandlerCreate(orgAnim, instance.GetComponent<Animator>());
            if (instancedModel == null)
            {
                blendProxy = instance.GetComponent<VRMBlendShapeProxy>();
                if (isMaster && LipSync.OVRLipSyncVRM.IsUseLipSync)
                    AttachLipSync(instance);

                instance.GetOrAddComponent<Facial.EyeCtrl>();
                var useFacial = Settings.ReadBool("UseFacial", true);
                if (isMaster && useFacial)
                    facialFace = instance.GetOrAddComponent<Facial.FaceCtrl>();
                instancedModel = instance;
            }
        }

        void AttachLipSync(GameObject vrmModel)
        {
            var ovrInstance = LipSync.OVRLipSyncVRM.Instance;
            ovrInstance.OnBlend.Subscribe(v => ovrInstance.BlendFunc(v, blendProxy)).AddTo(vrmModel);
        }

        void PoseHandlerCreate(Animator org, Animator vrm)
        {
            OnDestroy();
            orgPose = new HumanPoseHandler(org.avatar, org.transform);
            vrmPose = new HumanPoseHandler(vrm.avatar, vrm.transform);
        }

        void OnDestroy()
        {
            if (orgPose != null)
                orgPose.Dispose();
            if (vrmPose != null)
                vrmPose.Dispose();
        }

        void LateUpdate()
        {
            orgPose.GetHumanPose(ref hp);
            vrmPose.SetHumanPose(ref hp);
            instancedModel.transform.localPosition = Vector3.zero;
            instancedModel.transform.localRotation = Quaternion.identity;
            if (blendProxy)
                blendProxy.Apply();
        }
    }

    [HarmonyPatch(typeof(OcPl))]
    [HarmonyPatch("charaChangeSteup")]
    static class OcPlVRM
    {
        static GameObject vrmModel;

        static void Postfix(OcPl __instance)
        {
            var slave = __instance as OcPlSlave;
            if (slave && !slave.FindNameInParentRecursive("UI"))
            {
                var selfId = OcNetMng.Inst.NetPlId_Master;
                if (SingletonMonoBehaviour<OcPlMng>.Inst.getPlSlave(selfId - 1) != slave) return;
            }

            if (vrmModel == null)
            {
                //カスタムモデル名の取得(設定ファイルにないためLogの出力が不自然にならないよう調整)
                var ModelStr = Settings.ReadSettings("ModelName");
                var path = Environment.CurrentDirectory + @"\Player2VRM\player.vrm";
                if (ModelStr != null)
                    path = Environment.CurrentDirectory + @"\Player2VRM\" + ModelStr + ".vrm";

                try
                {
                    vrmModel = ImportVRM(path);
                }
                catch
                {
                    if (ModelStr != null)
                        UnityEngine.Debug.LogWarning("VRMファイルの読み込みに失敗しました。settings.txt内のModelNameを確認してください。");
                    else
                        UnityEngine.Debug.LogWarning("VRMファイルの読み込みに失敗しました。Player2VRMフォルダにplayer.vrmを配置してください。");
                    return;
                }

                var receiveShadows = Settings.ReadBool("ReceiveShadows");
                if (!receiveShadows)
                {
                    foreach (var smr in vrmModel.GetComponentsInChildren<SkinnedMeshRenderer>())
                    {
                        smr.receiveShadows = false;
                    }
                }

                // プレイヤースケール調整
                {
                    var scale = Settings.ReadFloat("PlayerScale", 1.0f);
                    __instance.transform.localScale *= scale;
                    vrmModel.transform.localScale /= scale;
                }
            }

            foreach (var smr in __instance.GetComponentsInChildren<SkinnedMeshRenderer>())
            {
                if (Settings.ReadBool("UseRealToonShader", false))
                {
                    foreach (var mat in smr.materials)
                    {
                        mat.SetFloat("_EnableTextureTransparent", 1.0f);
                    }
                }
                smr.enabled = false;
                Transform trans = smr.transform;
                while (vrmModel != null && trans != null)
                {
                    if (trans.name.Contains(vrmModel.name))
                    {
                        smr.enabled = true;
                        break;
                    }
                    trans = trans.parent;
                }
            }

            __instance.Animator.gameObject.GetOrAddComponent<CloneHumanoid>().Setup(vrmModel, __instance.Animator, __instance is OcPlMaster);
        }

        private static GameObject ImportVRM(string path)
        {
            var bytes = File.ReadAllBytes(path);
            var context = new VRMImporterContext();
            context.ParseGlb(bytes);

            try
            {
                context.Load();
            }
            catch { }

            // モデルスケール調整
            context.Root.transform.localScale *= Settings.ReadFloat("ModelScale", 1.0f);

            return context.Root;
        }
    }
}
