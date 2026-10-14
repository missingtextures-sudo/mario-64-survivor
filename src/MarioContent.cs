using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using EntityStates;
using RoR2;
using RoR2.ContentManagement;
using RoR2.Skills;
using UnityEngine;
using UnityEngine.AddressableAssets;
using UnityEngine.Networking;

namespace MarioRoR2
{
    /// <summary>
    /// Mario's survivor (sheets/survivor.json, sheets/skills.json) as a content pack: a clone of Commando's body
    /// (hurtboxes, item displays, networking) with Mario's stats, his own skill descriptions, MarioMain as its
    /// state and MarioController on it; a clone of Commando's display with a live Mario in it.
    /// </summary>
    internal sealed class MarioContent : IContentPackProvider
    {
        public static GameObject? BodyPrefab;
        public static SurvivorDef? Survivor;
        private static readonly Dictionary<string, string> tokens = new Dictionary<string, string>();
        private readonly ContentPack pack = new ContentPack();

        public string identifier => Plugin.Guid;

        public static void Register()
        {
            ContentManager.collectContentPackProviders += add => add(new MarioContent());
            Language.onCurrentLanguageChanged += ApplyTokens;
        }

        public IEnumerator LoadStaticContentAsync(LoadStaticContentAsyncArgs args)
        {
            try
            {
                Build();
            }
            catch (Exception e)
            {
                Plugin.Log.LogError("Mario's survivor couldn't be built: " + e);
            }
            args.ReportProgress(1f);
            yield break;
        }

        public IEnumerator GenerateContentPackAsync(GetContentPackAsyncArgs args)
        {
            ContentPack.Copy(pack, args.output);
            args.ReportProgress(1f);
            yield break;
        }

        public IEnumerator FinalizeAsync(FinalizeAsyncArgs args)
        {
            args.ReportProgress(1f);
            yield break;
        }

        private static GameObject Load(string legacyPath, string addressKey)
        {
            GameObject? go = null;
            try
            {
                go = LegacyResourcesAPI.Load<GameObject>(legacyPath);
            }
            catch (Exception e)
            {
                Plugin.Log.LogDebug($"{legacyPath}: {e.Message}");
            }
            if (go == null)
            {
                go = Addressables.LoadAssetAsync<GameObject>(addressKey).WaitForCompletion();
            }
            return go ?? throw new InvalidOperationException($"neither {legacyPath} nor {addressKey} loaded");
        }

        private void Build()
        {
            Sheets.SurvivorRow sv = Sheets.Survivor[0];
            const string P = "MARIO64_";
            var holder = new GameObject("MarioRoR2Prefabs");
            holder.SetActive(false);
            UnityEngine.Object.DontDestroyOnLoad(holder);

            GameObject commando = Load(sv.CloneBody, "RoR2/Base/Commando/CommandoBody.prefab");
            GameObject body = UnityEngine.Object.Instantiate(commando, holder.transform);
            body.name = sv.Id;
            SetAssetId(body, sv.Id);

            Renderer? cr = commando.GetComponentInChildren<SkinnedMeshRenderer>();
            MarioRenderer.LitShader = cr != null && cr.sharedMaterial != null ? cr.sharedMaterial.shader : LegacyShaderAPI.Find("Hopoo Games/Deferred/Standard");

            ColorUtility.TryParseHtmlString(sv.Color, out Color color);
            var cb = body.GetComponent<CharacterBody>();
            cb.baseNameToken = P + "NAME";
            cb.subtitleNameToken = P + "SUBTITLE";
            cb.bodyColor = color;
            cb.baseMaxHealth = sv.BaseMaxHealth;
            cb.levelMaxHealth = sv.LevelMaxHealth;
            cb.baseRegen = sv.BaseRegen;
            cb.levelRegen = sv.LevelRegen;
            cb.baseDamage = sv.BaseDamage;
            cb.levelDamage = sv.LevelDamage;
            cb.baseArmor = sv.BaseArmor;
            cb.baseCrit = sv.BaseCrit;
            if (Sm64.Ready)
            {
                cb.portraitIcon = BakedAtlas.Icon(color, 2, 128);
            }
            tokens[P + "NAME"] = sv.Name;
            tokens[P + "SUBTITLE"] = sv.Subtitle;
            tokens[P + "DESCRIPTION"] = (Sm64.Ready ? "" : $"<color=#FF6060>Mario can't play yet: {Sm64.Problem}</color>\n\n") + sv.Description;
            tokens[P + "OUTRO"] = sv.Outro;
            tokens[P + "FAILURE"] = sv.FailureOutro;

            foreach (EntityStateMachine esm in body.GetComponents<EntityStateMachine>())
            {
                if (esm.customName == "Body")
                {
                    esm.mainStateType = new SerializableEntityStateType(typeof(MarioMain));
                }
            }
            body.AddComponent<MarioController>();

            var skillDefs = new List<SkillDef>();
            var families = new List<SkillFamily>();
            SkillLocator sl = body.GetComponent<SkillLocator>();
            foreach (Sheets.SkillRow row in Sheets.Skills)
            {
                ColorUtility.TryParseHtmlString(row.IconColor, out Color ic);
                Sprite? icon = Sm64.Ready ? Sprite(BakedAtlas.Icon(ic, row.IconTile, 128)) : null;
                string nameTok = P + row.Id.ToUpperInvariant() + "_NAME", descTok = P + row.Id.ToUpperInvariant() + "_DESC";
                tokens[nameTok] = $"{row.Name} ({row.Button})";
                tokens[descTok] = row.Description;
                if (row.Slot == "passive")
                {
                    sl.passiveSkill = new SkillLocator.PassiveSkill { enabled = true, skillNameToken = nameTok, skillDescriptionToken = descTok, icon = icon };
                    continue;
                }
                GenericSkill gs = row.Slot switch
                {
                    "primary" => sl.primary,
                    "secondary" => sl.secondary,
                    "utility" => sl.utility,
                    _ => sl.special,
                };
                var sd = ScriptableObject.CreateInstance<SkillDef>();
                ((ScriptableObject)sd).name = "Mario64" + row.Id;
                sd.skillName = "Mario64" + row.Id;
                sd.skillNameToken = nameTok;
                sd.skillDescriptionToken = descTok;
                sd.icon = icon;
                sd.activationStateMachineName = "Weapon";
                sd.activationState = new SerializableEntityStateType(typeof(Idle)); // never activated: SM64 does the move
                sd.baseRechargeInterval = 0f;
                sd.baseMaxStock = 1;
                sd.isCombatSkill = false;
                sd.hideStockCount = true;
                sd.hideCooldown = true;
                var fam = ScriptableObject.CreateInstance<SkillFamily>();
                ((ScriptableObject)fam).name = "Mario64" + row.Slot + "Family";
                fam.variants = new[] { new SkillFamily.Variant { skillDef = sd, viewableNode = new ViewablesCatalog.Node(nameTok, false, null) } };
                gs._skillFamily = fam;
                skillDefs.Add(sd);
                families.Add(fam);
            }

            GameObject commandoDisplay = Load(sv.CloneDisplay, "RoR2/Base/Commando/CommandoDisplay.prefab");
            GameObject display = UnityEngine.Object.Instantiate(commandoDisplay, holder.transform);
            display.name = "MarioDisplay";
            display.AddComponent<MarioDisplay>();

            var survivor = ScriptableObject.CreateInstance<SurvivorDef>();
            survivor.cachedName = "Mario64";
            survivor.bodyPrefab = body;
            survivor.displayPrefab = display;
            survivor.displayNameToken = P + "NAME";
            survivor.descriptionToken = P + "DESCRIPTION";
            survivor.outroFlavorToken = P + "OUTRO";
            survivor.mainEndingEscapeFailureFlavorToken = P + "FAILURE";
            survivor.primaryColor = color;
            survivor.desiredSortPosition = sv.SortPosition;

            pack.bodyPrefabs.Add(new[] { body });
            pack.survivorDefs.Add(new[] { survivor });
            pack.skillDefs.Add(skillDefs.ToArray());
            pack.skillFamilies.Add(families.ToArray());
            pack.entityStateTypes.Add(new[] { typeof(MarioMain) });
            BodyPrefab = body;
            Survivor = survivor;
            ApplyTokens();
            Plugin.Log.LogInfo($"survivor: {sv.Name} built ({skillDefs.Count} skills{(Sm64.Ready ? "" : "; libsm64 not ready: " + Sm64.Problem)})");
        }

        private static Sprite Sprite(Texture2D t) => UnityEngine.Sprite.Create(t, new Rect(0, 0, t.width, t.height), new Vector2(0.5f, 0.5f));

        /// <summary>A unique network asset id, as other modded bodies get, so the clone isn't taken for Commando.</summary>
        private static void SetAssetId(GameObject go, string name)
        {
            var ni = go.GetComponent<NetworkIdentity>();
            FieldInfo? f = typeof(NetworkIdentity).GetField("m_AssetId", BindingFlags.Instance | BindingFlags.NonPublic);
            if (ni == null || f == null)
            {
                return;
            }
            using var md5 = System.Security.Cryptography.MD5.Create();
            byte[] h = md5.ComputeHash(System.Text.Encoding.UTF8.GetBytes(Plugin.Guid + "/" + name));
            f.SetValue(ni, NetworkHash128.Parse(BitConverter.ToString(h).Replace("-", "").ToLowerInvariant()));
        }

        private static void ApplyTokens()
        {
            foreach (Language lang in Language.GetAllLanguages())
            {
                foreach (var kv in tokens)
                {
                    lang.SetStringByToken(kv.Key, kv.Value);
                }
            }
        }
    }
}
