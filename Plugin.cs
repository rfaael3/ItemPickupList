using System.Collections.Generic;
using System.IO;
using System.Reflection;
using BepInEx;
using BepInEx.Configuration;
using HarmonyLib;
using RiskOfOptions;
using RiskOfOptions.OptionConfigs;
using RiskOfOptions.Options;
using RoR2;
using RoR2.UI;
using RoR2.ContentManagement;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.AddressableAssets;

namespace ItemPickupList
{
    public enum ListPosition
    {
        TopLeft, TopCenter, TopRight,
        MiddleLeft, Center, MiddleRight,
        BottomLeft, BottomCenter, BottomRight
    }

    [BepInDependency("com.rune580.riskofoptions")]
    [BepInPlugin("rfaael.ItemPickupList", "ItemPickupList", "0.3.10")]
    public class Plugin : BaseUnityPlugin
    {
        public static ConfigEntry<bool> Enabled;
        public static ConfigEntry<ListPosition> Position;
        public static ConfigEntry<float> OffsetX;
        public static ConfigEntry<float> OffsetY;
        public static ConfigEntry<float> Scale;
        public static ConfigEntry<float> BaseDuration;
        public static ConfigEntry<float> SpeedPerQueued;
        public static ConfigEntry<float> MaxSpeed;
        public static ConfigEntry<float> SpawnInterval;
        public static ConfigEntry<int> MaxVisible;
        public static ConfigEntry<KeyboardShortcut> TestKey;
        internal static BepInEx.Logging.ManualLogSource Log;

        private Harmony harmony;
        private Texture2D modIconTexture;
        private Sprite modIconSprite;
        private static FieldInfo notificationDataField;
        private static FieldInfo transformationField;
        private static bool receivedNotification;
        private static readonly HashSet<System.Type> unknownDataTypes = new HashSet<System.Type>();
        private bool keyWasDown;

        private void Awake()
        {
            Log = Logger;

            Enabled        = Config.Bind("General", "Enabled", true, "Enable the pickup list. When disabled, use the default game notifications.");
            Position       = Config.Bind("Position", "Preset", ListPosition.MiddleLeft, "Screen anchor for the pickup list.");
            OffsetX        = Config.Bind("Position", "X", 0f, "Horizontal offset from the selected anchor (positive = right).");
            OffsetY        = Config.Bind("Position", "Y", 0f, "Vertical offset from the selected anchor (positive = up).");
            Scale          = Config.Bind("Position", "Scale", 1f, "Scale of the pickup list.");
            BaseDuration   = Config.Bind("List", "Base duration", 4f, "Seconds each entry remains visible without queue acceleration.");
            SpeedPerQueued = Config.Bind("List", "Acceleration per item", 0.35f, "Speed increase for each additional queued or visible entry.");
            MaxSpeed       = Config.Bind("List", "Maximum speed", 6f, "Maximum notification speed multiplier.");
            SpawnInterval  = Config.Bind("List", "Entry interval", 0.35f, "Seconds between new entries without queue acceleration.");
            MaxVisible     = Config.Bind("List", "Maximum visible", 6, "Maximum number of entries visible at the same time.");
            TestKey        = Config.Bind("Preview", "Preview shortcut", new KeyboardShortcut(KeyCode.F8), "Shortcut to preview example items in the list.");

            TestKey.SettingChanged += TestKeyChanged;
            RegisterOptions();
            RegisterModIcon();

            PickupListUI.Create();   // Custom overlay, independent of the game HUD.
            PatchNotifications();
        }

        private void Update()
        {
            if (!Application.isFocused)
            {
                // After Alt+Tab, wait for release before accepting another press.
                keyWasDown = true;
                return;
            }

            var shortcut = TestKey.Value;
            if (shortcut.MainKey == KeyCode.None)
            {
                keyWasDown = false;
                return;
            }

            // Read the main key separately: BepInEx IsPressed()
            // rejects additional held keys during movement or combat.
            bool down = Input.GetKey(shortcut.MainKey);
            bool justPressed = down && !keyWasDown;
            keyWasDown = down;
            if (!justPressed) return;

            // Require configured modifiers, but allow other held keys.
            foreach (var modifier in shortcut.Modifiers)
            {
                if (modifier != KeyCode.None && !Input.GetKey(modifier))
                {
                    Log.LogInfo("Preview skipped: hold " + modifier +
                        ". Configured shortcut: " + shortcut);
                    return;
                }
            }

            Log.LogInfo("Preview shortcut pressed: " + shortcut);
            TestList();
        }

        private void TestKeyChanged(object sender, System.EventArgs args)
        {
            // Do not preview using the same press that assigned the shortcut.
            var key = TestKey.Value.MainKey;
            keyWasDown = key != KeyCode.None && Input.GetKey(key);
            Log.LogInfo("Preview shortcut updated: " + TestKey.Value);
        }

        private void RegisterModIcon()
        {
            try
            {
                // Embed the icon in the DLL so manual installations need no extra files.
                using (var stream = typeof(Plugin).Assembly.GetManifestResourceStream("ItemPickupList.ModIcon.png"))
                {
                    if (stream == null) throw new FileNotFoundException("Embedded mod icon was not found.");
                    using (var buffer = new MemoryStream())
                    {
                        stream.CopyTo(buffer);
                        modIconTexture = new Texture2D(2, 2, TextureFormat.RGBA32, false);
                        if (!ImageConversion.LoadImage(modIconTexture, buffer.ToArray(), true))
                            throw new System.InvalidOperationException("Embedded mod icon is not a valid image.");
                    }
                }

                modIconTexture.name = "ItemPickupList Mod Icon";
                modIconTexture.hideFlags = HideFlags.HideAndDontSave;
                modIconTexture.wrapMode = TextureWrapMode.Clamp;
                modIconTexture.filterMode = FilterMode.Bilinear;
                modIconSprite = Sprite.Create(modIconTexture,
                    new Rect(0f, 0f, modIconTexture.width, modIconTexture.height),
                    new Vector2(0.5f, 0.5f), 100f);
                modIconSprite.name = "ItemPickupList Mod Icon";
                modIconSprite.hideFlags = HideFlags.HideAndDontSave;
                ModSettingsManager.SetModIcon(modIconSprite, "rfaael.ItemPickupList", "ItemPickupList");
            }
            catch (System.Exception ex)
            {
                if (modIconSprite) Destroy(modIconSprite);
                if (modIconTexture) Destroy(modIconTexture);
                modIconSprite = null;
                modIconTexture = null;
                Log.LogWarning("Failed to register the Risk of Options mod icon: " + ex);
            }
        }

        private void ResetToDefaults()
        {
            bool saveOnConfigSet = Config.SaveOnConfigSet;
            try
            {
                // Use the defaults declared in Config.Bind, not previously saved values.
                Config.SaveOnConfigSet = false;
                foreach (var setting in Config)
                    setting.Value.BoxedValue = setting.Value.DefaultValue;

                Config.Save();
                Log.LogInfo("All ItemPickupList settings have been restored to defaults.");
            }
            catch (System.Exception ex)
            {
                Log.LogError("Failed to restore default settings: " + ex);
            }
            finally
            {
                Config.SaveOnConfigSet = saveOnConfigSet;
            }
        }

        private void RegisterOptions()
        {
            ModSettingsManager.SetModDescription("Displays pickups in a configurable HUD list that speeds up as the queue grows.");

            ModSettingsManager.AddOption(new CheckBoxOption(Enabled));
            ModSettingsManager.AddOption(new GenericButtonOption("Reset to defaults", "General",
                "Restore all ItemPickupList settings to their original defaults, including position, speed, visibility, and the F8 preview shortcut. Changes are saved immediately. If a control still shows its old value, reopen the mod settings page.",
                "Reset all", ResetToDefaults));

            ModSettingsManager.AddOption(new ChoiceOption(Position));
            ModSettingsManager.AddOption(new StepSliderOption(OffsetX,
                new StepSliderConfig { min = -1200f, max = 1200f, increment = 5f, FormatString = "{0:0}" }));
            ModSettingsManager.AddOption(new StepSliderOption(OffsetY,
                new StepSliderConfig { min = -700f, max = 700f, increment = 5f, FormatString = "{0:0}" }));
            ModSettingsManager.AddOption(new SliderOption(Scale,
                new SliderConfig { min = 0.5f, max = 2f, FormatString = "{0:0.00}x" }));
            ModSettingsManager.AddOption(new GenericButtonOption("Preview list", "Position",
                "Show five random example items to preview the list position.",
                "Preview", TestList));
            ModSettingsManager.AddOption(new KeyBindOption(TestKey));

            ModSettingsManager.AddOption(new SliderOption(BaseDuration,
                new SliderConfig { min = 0.5f, max = 15f, FormatString = "{0:0.0}s" }));
            ModSettingsManager.AddOption(new SliderOption(SpeedPerQueued,
                new SliderConfig { min = 0f, max = 2f, FormatString = "{0:0.00}" }));
            ModSettingsManager.AddOption(new SliderOption(MaxSpeed,
                new SliderConfig { min = 1f, max = 15f, FormatString = "{0:0.0}x" }));
            ModSettingsManager.AddOption(new SliderOption(SpawnInterval,
                new SliderConfig { min = 0.05f, max = 2f, FormatString = "{0:0.00}s" }));
            ModSettingsManager.AddOption(new IntSliderOption(MaxVisible,
                new IntSliderConfig { min = 1, max = 15 }));
        }

        public static void GetAnchor(ListPosition p, out float ax, out float ay)
        {
            switch (p)
            {
                case ListPosition.TopLeft:      ax = 0f;   ay = 1f;   break;
                case ListPosition.TopCenter:    ax = 0.5f; ay = 1f;   break;
                case ListPosition.TopRight:     ax = 1f;   ay = 1f;   break;
                case ListPosition.MiddleLeft:   ax = 0f;   ay = 0.5f; break;
                case ListPosition.Center:       ax = 0.5f; ay = 0.5f; break;
                case ListPosition.MiddleRight:  ax = 1f;   ay = 0.5f; break;
                case ListPosition.BottomLeft:   ax = 0f;   ay = 0f;   break;
                case ListPosition.BottomCenter: ax = 0.5f; ay = 0f;   break;
                default:                        ax = 1f;   ay = 0f;   break; // BottomRight
            }
        }

        // Hook the local queue entry point, including notifications that do not
        // pass through the static PushPickupNotification helpers.
        private void PatchNotifications()
        {
            harmony = new Harmony("rfaael.ItemPickupList");
            int patched = 0;
            foreach (var method in typeof(CharacterMasterNotificationQueue).GetMethods(
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
            {
                if (method.Name != "PushNotification" || method.ReturnType != typeof(void)) continue;
                var parameters = method.GetParameters();
                if (parameters.Length != 2 || parameters[0].ParameterType.Name != "NotificationInfo" ||
                    parameters[1].ParameterType != typeof(float)) continue;

                // Validate the layout before installing the prefix. If the API changes,
                // log the mismatch and keep the original notifications working.
                var infoType = parameters[0].ParameterType;
                notificationDataField = AccessTools.Field(infoType, "data");
                transformationField = AccessTools.Field(infoType, "transformation");
                if (notificationDataField == null || transformationField == null)
                {
                    Log.LogError("Unsupported NotificationInfo format. Available fields:");
                    foreach (var field in infoType.GetFields(
                        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
                        Log.LogError(field.FieldType.FullName + " " + field.Name);
                    continue;
                }

                try
                {
                    harmony.Patch(method, prefix: new HarmonyMethod(
                        typeof(Plugin).GetMethod(nameof(NotificationPrefix),
                            BindingFlags.Static | BindingFlags.NonPublic)));
                    patched++;
                    Log.LogInfo("Hooked local notification queue: " + method);
                }
                catch (System.Exception ex)
                {
                    Log.LogError("Failed to hook " + method + ": " + ex);
                }
            }

            if (patched == 0)
            {
                Log.LogError("No notification queue was hooked. Include this log in your bug report.");
                foreach (var method in typeof(CharacterMasterNotificationQueue).GetMethods(
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static))
                    if (method.Name.StartsWith("Push")) Log.LogInfo("Available API: " + method);
            }
        }

        // __0 is the first argument, regardless of its name in the game assembly.
        private static bool NotificationPrefix(CharacterMasterNotificationQueue __instance, object __0)
        {
            try
            {
                if (!Enabled.Value || !__instance || __0 == null) return true;
                if (!receivedNotification)
                {
                    receivedNotification = true;
                    Log.LogInfo("The queue received a game notification.");
                }

                // Keep transformations, unlocks, and artifacts on the default HUD.
                if (transformationField.GetValue(__0) != null) return true;
                var master = __instance.GetComponent<CharacterMaster>();
                if (!master || !IsLocalMaster(master)) return true;

                object source = notificationDataField.GetValue(__0);
                PickupData data;
                if (!TryBuildData(master, source, out data))
                {
                    if (source != null && unknownDataTypes.Add(source.GetType()))
                        Log.LogInfo("Notification kept on the default HUD: " + source.GetType().FullName);
                    return true;
                }

                bool handled = Enqueue(master, data);
                Log.LogInfo("Pickup: " + data.Name + " -> " + (handled ? "list" : "default HUD"));
                return !handled;
            }
            catch (System.Exception ex)
            {
                Log.LogError("Failed to process notification (using the default HUD): " + ex);
                return true;
            }
        }

        private void OnDestroy()
        {
            if (TestKey != null) TestKey.SettingChanged -= TestKeyChanged;
            if (harmony != null) harmony.UnpatchSelf();
            if (PickupListUI.Instance) Destroy(PickupListUI.Instance.gameObject);
            PickupListUI.Instance = null;
            if (modIconSprite) Destroy(modIconSprite);
            if (modIconTexture) Destroy(modIconTexture);
        }

        private static bool TryBuildData(CharacterMaster master, object arg, out PickupData data)
        {
            data = default(PickupData);

            // Game versions may store definitions in NotificationInfo.data
            // instead of the indices accepted by static helpers.
            if (arg is ItemDef itemDef)
                return TryItem(master, itemDef.itemIndex, out data);
            if (arg is EquipmentDef equipmentDef)
                return TryEquipment(equipmentDef.equipmentIndex, out data);
            if (arg is PickupDef pickupDef)
            {
                if (pickupDef.itemIndex != ItemIndex.None)
                    return TryItem(master, pickupDef.itemIndex, out data);
                if (pickupDef.equipmentIndex != EquipmentIndex.None)
                    return TryEquipment(pickupDef.equipmentIndex, out data);
                return false;
            }

            if (arg is ItemIndex)
                return TryItem(master, (ItemIndex)arg, out data);

            if (arg is EquipmentIndex)
                return TryEquipment((EquipmentIndex)arg, out data);

            if (arg is PickupIndex)
            {
                var pd = PickupCatalog.GetPickupDef((PickupIndex)arg);
                if (pd == null) return false;
                if (pd.itemIndex != ItemIndex.None) return TryItem(master, pd.itemIndex, out data);
                if (pd.equipmentIndex != EquipmentIndex.None) return TryEquipment(pd.equipmentIndex, out data);
            }
            return false;
        }

        private static bool TryItem(CharacterMaster master, ItemIndex idx, out PickupData data)
        {
            data = default(PickupData);
            var def = ItemCatalog.GetItemDef(idx);
            if (def == null) return false;

            // Use pickup color; the icon may need asynchronous loading.
            var pd = PickupCatalog.GetPickupDef(PickupCatalog.FindPickupIndex(idx));

            int count = (master != null && master.inventory != null) ? master.inventory.GetItemCountEffective(idx) : 1;
            data = new PickupData
            {
                Icon = def.pickupIconSprite,
                IconReference = def.pickupIconSpriteReference,
                Name = Language.GetString(def.nameToken),
                Desc = Language.GetString(def.pickupToken),
                Color = pd != null ? pd.baseColor : Color.white,
                Count = count
            };
            return true;
        }

        private static bool TryEquipment(EquipmentIndex idx, out PickupData data)
        {
            data = default(PickupData);
            var def = EquipmentCatalog.GetEquipmentDef(idx);
            if (def == null) return false;
            var pd = PickupCatalog.GetPickupDef(PickupCatalog.FindPickupIndex(idx));

            data = new PickupData
            {
                Icon = def.pickupIconSprite,
                IconReference = def.pickupIconSpriteReference,
                Name = Language.GetString(def.nameToken),
                Desc = Language.GetString(def.pickupToken),
                Color = pd != null ? pd.baseColor : Color.white,
                Count = 1
            };
            return true;
        }

        // Identify the local owner without depending on HUD visibility.
        // The camera can target another player while spectating.
        private static bool IsLocalMaster(CharacterMaster master)
        {
            foreach (var localUser in LocalUserManager.readOnlyLocalUsersList)
                if (localUser != null && localUser.cachedMaster == master) return true;

            var controller = master.playerCharacterMasterController;
            return controller && controller.networkUser && controller.networkUser.localUser != null;
        }

        private static bool Enqueue(CharacterMaster master, PickupData data)
        {
            var ui = PickupListUI.Instance;
            if (!ui || !IsLocalMaster(master)) return false;

            // Use a single interception point so legitimate pickups in the same frame
            // are not discarded just because their names and counts match.
            ui.Enqueue(data);
            return true;
        }

        // Preview five random items from the shortcut or Risk of Options button.
        private static void TestList()
        {
            var ui = PickupListUI.Instance;
            if (!ui) { Log.LogWarning("Preview: the list has not been created yet."); return; }
            if (ItemCatalog.itemCount <= 0)
            {
                Log.LogWarning("Preview: the item catalog is still loading. Try again once the menu has loaded.");
                return;
            }

            int added = 0, tries = 0;
            while (added < 5 && tries < 200)
            {
                tries++;
                var idx = (ItemIndex)Random.Range(0, ItemCatalog.itemCount);
                try
                {
                    var def = ItemCatalog.GetItemDef(idx);
                    if (def == null || def.hidden) continue;
                    PickupData data;
                    if (!TryItem(null, idx, out data)) continue;
                    ui.Enqueue(data);
                    added++;
                }
                catch (System.Exception ex)
                {
                    Log.LogWarning("Preview: failed to prepare item " + idx + ": " + ex);
                }
            }
            if (added == 0)
                Log.LogWarning("Preview: no valid items were found in the catalog.");
            else
                Log.LogInfo("Preview: " + added + " items added to the list.");
        }
    }

    public struct PickupData
    {
        public Sprite Icon;
        public AssetReferenceT<Sprite> IconReference;
        public string Name;
        public string Desc;
        public Color Color;
        public int Count;
    }

    // Keep the sprite reference alive until its row is destroyed.
    public class PickupIconLoader : MonoBehaviour
    {
        private AssetOrDirectReference<Sprite> loadedIcon;
        private Image image;

        public void Load(Image target, AssetReferenceT<Sprite> reference, Sprite direct)
        {
            image = target;
            Show(direct);
            if (reference == null || !reference.RuntimeKeyIsValid()) return;

            try
            {
                loadedIcon = AssetOrDirectReference<Sprite>.GetNewInstance(
                    reference, true, direct, Show, null);
            }
            catch (System.Exception ex)
            {
                // Icon failures must not prevent the notification from appearing.
                Plugin.Log.LogWarning("Failed to load icon: " + ex);
            }
        }

        private void Show(Sprite sprite)
        {
            if (!image) return; // The row may have expired before loading finished.
            image.sprite = sprite;
            image.enabled = sprite != null;
        }

        private void OnDestroy()
        {
            image = null;
            if (loadedIcon != null)
            {
                loadedIcon.Reset();
                loadedIcon = null;
            }
        }
    }

    // Independent overlay for the pickup list; pausing or changing stages
    // does not destroy the canvas.
    public class PickupListUI : MonoBehaviour
    {
        public static PickupListUI Instance;

        private const float Width = 380f;
        private const float Height = 56f;
        private const float Gap = 6f;
        private const float Margin = 20f;      // Automatic margin from the selected screen edge.
        private const float SlideTime = 0.25f; // Measured in entry age units.
        private const float FadeTime = 0.5f;

        private class Entry
        {
            public GameObject Go;
            public RectTransform Rt;
            public CanvasGroup Group;
            public float Age;
            public float CurY;
        }

        private RectTransform root;
        private TMP_FontAsset font;
        private readonly Queue<PickupData> waiting = new Queue<PickupData>();
        private readonly List<Entry> active = new List<Entry>();
        private float spawnTimer;
        private bool errorLogged;

        public static void Create()
        {
            var go = new GameObject("ItemPickupListCanvas", typeof(Canvas), typeof(CanvasScaler));
            DontDestroyOnLoad(go);

            var canvas = go.GetComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 500;

            var scaler = go.GetComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920f, 1080f);
            scaler.screenMatchMode = CanvasScaler.ScreenMatchMode.MatchWidthOrHeight;
            scaler.matchWidthOrHeight = 0.5f;

            Instance = go.AddComponent<PickupListUI>();
        }

        private void Awake()
        {
            var rootGo = new GameObject("ItemPickupListRoot", typeof(RectTransform));
            root = (RectTransform)rootGo.transform;
            root.SetParent(transform, false);
            root.sizeDelta = new Vector2(Width, 0f);
        }

        public void Enqueue(PickupData data) { waiting.Enqueue(data); }

        private TMP_FontAsset FindFont()
        {
            foreach (var t in Resources.FindObjectsOfTypeAll<HGTextMeshProUGUI>())
                if (t && t.font) return t.font;
            return TMP_Settings.defaultFontAsset;
        }

        // Row Y coordinate relative to the selected anchor.
        private static float TargetY(int i, int count, float ay)
        {
            float step = Height + Gap;
            if (ay >= 1f) return -(Height * 0.5f) - i * step;                 // Top: grow downward.
            if (ay <= 0f) return (Height * 0.5f) + (count - 1 - i) * step;    // Bottom: grow upward.
            return ((count - 1) * 0.5f - i) * step;                           // Middle: center the rows.
        }

        private void Update()
        {
            // Log failures instead of silently freezing the list.
            try { Tick(); }
            catch (System.Exception ex)
            {
                if (!errorLogged)
                {
                    errorLogged = true;
                    Plugin.Log.LogError("Pickup list error: " + ex);
                }
            }
        }

        private void Tick()
        {
            if (!root) return;

            float dt = Time.unscaledDeltaTime;

            // Read position and scale every frame to apply settings immediately.
            float ax, ay;
            Plugin.GetAnchor(Plugin.Position.Value, out ax, out ay);
            float mx = ax < 0.5f ? Margin : (ax > 0.5f ? -Margin : 0f);
            float my = ay < 0.5f ? Margin : (ay > 0.5f ? -Margin : 0f);
            var anchor = new Vector2(ax, ay);
            root.anchorMin = root.anchorMax = root.pivot = anchor;
            root.anchoredPosition = new Vector2(mx + Plugin.OffsetX.Value, my + Plugin.OffsetY.Value);
            root.localScale = Vector3.one * Plugin.Scale.Value;

            // Remove entries whose objects were destroyed externally.
            for (int i = active.Count - 1; i >= 0; i--)
                if (active[i].Go == null) active.RemoveAt(i);

            // More queued and visible entries increase notification speed.
            int load = waiting.Count + active.Count;
            float speed = 1f + Mathf.Max(0, load - 1) * Plugin.SpeedPerQueued.Value;
            speed = Mathf.Min(speed, Plugin.MaxSpeed.Value);

            // Spawn new entries.
            spawnTimer -= dt * speed;
            if (waiting.Count > 0 && active.Count < Plugin.MaxVisible.Value && spawnTimer <= 0f)
            {
                var data = waiting.Dequeue();
                spawnTimer = Plugin.SpawnInterval.Value;
                try
                {
                    var e = Spawn(data);
                    active.Add(e);
                    e.CurY = TargetY(active.Count - 1, active.Count, ay);
                }
                catch (System.Exception ex)
                {
                    Plugin.Log.LogError("Failed to create list entry (" + data.Name + "): " + ex);
                }
            }

            float duration = Plugin.BaseDuration.Value;

            for (int i = active.Count - 1; i >= 0; i--)
            {
                var e = active[i];
                e.Age += dt * speed;
                if (e.Age >= duration)
                {
                    Destroy(e.Go);
                    active.RemoveAt(i);
                }
            }

            float dir = ax < 0.5f ? -1f : (ax > 0.5f ? 1f : 0f); // Screen side used for the slide-in animation.
            var entryAnchor = new Vector2(ax, 0.5f);

            for (int i = 0; i < active.Count; i++)
            {
                var e = active[i];
                float targetY = TargetY(i, active.Count, ay);
                e.CurY = Mathf.Lerp(e.CurY, targetY, 1f - Mathf.Exp(-14f * dt));

                e.Rt.anchorMin = e.Rt.anchorMax = e.Rt.pivot = entryAnchor;

                float t = Mathf.Clamp01(e.Age / SlideTime);
                float ease = 1f - (1f - t) * (1f - t);
                float x = dir * Mathf.Lerp(Width + 30f, 0f, ease);
                e.Rt.anchoredPosition = new Vector2(x, e.CurY);

                float fadeOut = Mathf.Clamp01((duration - e.Age) / FadeTime);
                e.Group.alpha = Mathf.Min(t, fadeOut);
            }
        }

        private Entry Spawn(PickupData d)
        {
            if (!font) font = FindFont();

            var go = new GameObject("Pickup", typeof(RectTransform), typeof(CanvasGroup), typeof(Image));
            try
            {
                var rt = (RectTransform)go.transform;
                rt.SetParent(root, false);
                rt.sizeDelta = new Vector2(Width, Height);
                go.GetComponent<Image>().color = new Color(0f, 0f, 0f, 0.55f);

                // Icon.
                var iconGo = new GameObject("Icon", typeof(RectTransform), typeof(Image));
                var iconRt = (RectTransform)iconGo.transform;
                iconRt.SetParent(rt, false);
                iconRt.anchorMin = iconRt.anchorMax = new Vector2(0f, 0.5f);
                iconRt.pivot = new Vector2(0f, 0.5f);
                iconRt.sizeDelta = new Vector2(Height - 8f, Height - 8f);
                iconRt.anchoredPosition = new Vector2(4f, 0f);
                var icon = iconGo.GetComponent<Image>();
                icon.preserveAspect = true;
                icon.raycastTarget = false;
                iconGo.AddComponent<PickupIconLoader>().Load(icon, d.IconReference, d.Icon);

                // Text.
                var textGo = new GameObject("Text", typeof(RectTransform), typeof(HGTextMeshProUGUI));
                var textRt = (RectTransform)textGo.transform;
                textRt.SetParent(rt, false);
                textRt.anchorMin = Vector2.zero;
                textRt.anchorMax = Vector2.one;
                textRt.offsetMin = new Vector2(Height + 4f, 2f);
                textRt.offsetMax = new Vector2(-6f, -2f);
                var tmp = textGo.GetComponent<HGTextMeshProUGUI>();
                if (font) tmp.font = font;
                tmp.fontSize = 18f;
                tmp.alignment = TextAlignmentOptions.MidlineLeft;
                tmp.enableWordWrapping = false;
                tmp.overflowMode = TextOverflowModes.Ellipsis;
                string hex = ColorUtility.ToHtmlStringRGB(d.Color);
                string stack = d.Count > 1 ? " <size=80%>x" + d.Count + "</size>" : "";
                tmp.text = "<color=#" + hex + ">" + d.Name + "</color>" + stack +
                           "\n<size=70%><color=#CCCCCC>" + d.Desc + "</color></size>";

                var group = go.GetComponent<CanvasGroup>();
                group.alpha = 0f;
                group.blocksRaycasts = false;
                group.interactable = false;

                return new Entry { Go = go, Rt = rt, Group = group, Age = 0f, CurY = 0f };
            }
            catch
            {
                Destroy(go); // Do not leave a partially created row on screen.
                throw;
            }
        }
    }
}
