using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Oxide.Core;
using Oxide.Core.Libraries;
using Oxide.Core.Plugins;
using Oxide.Game.Rust.Cui;
using UnityEngine;

namespace Oxide.Plugins
{
    [Info("RSurvivalStore", "RustInnovate", "2.9.6")]
    [Description(
        "Клиент RSurvivalStore с пользовательским интерфейсом на основе изображений, ScrollView и WipeBlock."
    )]
    public partial class RSurvivalStore : RustPlugin
    {
        private Configuration _config;
        private Dictionary<string, string> _imageCache = new Dictionary<string, string>();
        private Dictionary<string, string> _slotStates = new Dictionary<string, string>();
        private Dictionary<string, JArray> _playerItems = new Dictionary<string, JArray>();
        private Dictionary<ulong, string> _activeAddonParent = new Dictionary<ulong, string>();
        private bool _isRegistered = false;

        #region Configuration

        private class Configuration
        {
            [JsonProperty("Настройка магазина SurvivalShop")]
            public ShopConfig Settings { get; set; } = new ShopConfig();

            [JsonProperty("Настройка блокировки товаров после вайпа")]
            public List<WipeBlockItem> WipeBlocks { get; set; } = new List<WipeBlockItem>();

            [JsonProperty("Настройки дизайна (Изображения)")]
            public UIDesignConfig Design { get; set; } = new UIDesignConfig();
        }

        private class ShopConfig
        {
            [JsonProperty("SiteID")]
            public string SiteID { get; set; } = "";

            [JsonProperty("SiteKey")]
            public string SiteKey { get; set; } = "";

            [JsonProperty("Ссылка на магазин (для уведомлений)")]
            public string ShopURL { get; set; } = "https://myshop.survivalshop.org";

            [JsonProperty("Запретить открывать корзину магазина в зоне чужого шкафа?")]
            public bool BlockInEnemyTC { get; set; } = false;

            [JsonProperty("Запретить открывать корзину магазина во время рейдблока?")]
            public bool BlockInRaid { get; set; } = true;

            [JsonProperty("Запретить открывать корзину магазина во время комбатблока?")]
            public bool BlockInCombat { get; set; } = true;
        }

        private class WipeBlockItem
        {
            [JsonProperty("Имя предмета (опционально)")]
            public string Name { get; set; } = "";

            [JsonProperty("ID товара (ОБЯЗАТЕЛЬНО)")]
            public string ItemId { get; set; } = "";

            [JsonProperty("Время блокировки (в часах) (ОБЯЗАТЕЛЬНО)")]
            public float BlockHours { get; set; } = 24f;
        }

        private class UIDesignConfig
        {
            [JsonProperty("1. Панель фон под заголовок")]
            public UIDesignElement Header { get; set; } =
                new UIDesignElement
                {
                    ImageName = "header",
                    Width = 700,
                    Height = 60,
                    OffsetX = 0,
                    OffsetY = 280,
                };

            [JsonProperty("2. Панель фон главной корзины")]
            public UIDesignElement MainBg { get; set; } =
                new UIDesignElement
                {
                    ImageName = "main_bg",
                    Width = 700,
                    Height = 500,
                    OffsetX = 0,
                    OffsetY = -10,
                };

            [JsonProperty("3. Ячейка под предметы")]
            public UIDesignElement ItemSlot { get; set; } =
                new UIDesignElement
                {
                    ImageName = "item_slot",
                    Width = 120,
                    Height = 120,
                    OffsetX = 0,
                    OffsetY = 0,
                };

            [JsonProperty("3.1. Ячейка фон чертежа (Аффикс)")]
            public UIDesignElement ItemBlueprint { get; set; } =
                new UIDesignElement
                {
                    ImageName = "item_blueprint",
                    Width = 120,
                    Height = 120,
                    OffsetX = 0,
                    OffsetY = 0,
                };

            [JsonProperty("4. Ячейка фон успешно взятого предмета")]
            public UIDesignElement ItemSuccess { get; set; } =
                new UIDesignElement
                {
                    ImageName = "item_success",
                    Width = 120,
                    Height = 120,
                    OffsetX = 0,
                    OffsetY = 0,
                };

            [JsonProperty("5. Ячейка фон заблокированного предмета")]
            public UIDesignElement ItemBlocked { get; set; } =
                new UIDesignElement
                {
                    ImageName = "block_wipe",
                    Width = 120,
                    Height = 120,
                    OffsetX = 0,
                    OffsetY = 0,
                };

            [JsonProperty("6. Ячейка фон нехватки места")]
            public UIDesignElement ItemNoSpace { get; set; } =
                new UIDesignElement
                {
                    ImageName = "item_nospace", // The user can change this to whatever their red error image is
                    Width = 120,
                    Height = 120,
                    OffsetX = 0,
                    OffsetY = 0,
                };

            [JsonProperty("7. Кнопка Забрать всё")]
            public UIDesignElement TakeAllButton { get; set; } =
                new UIDesignElement
                {
                    ImageName = "take_all",
                    Width = 40,
                    Height = 40,
                    OffsetX = -330f,
                    OffsetY = -285f,
                };

            [JsonProperty("8. Иконка корзины (HUD)")]
            public UIDesignElement CartIcon { get; set; } =
                new UIDesignElement
                {
                    ImageName = "store",
                    Width = 40f,
                    Height = 40f,
                    OffsetX = -615f,
                    OffsetY = 340f,
                };
        }

        private class UIDesignElement
        {
            [JsonProperty("Название файла")]
            public string ImageName { get; set; }

            [JsonProperty("Ширина")]
            public float Width { get; set; }

            [JsonProperty("Высота")]
            public float Height { get; set; }

            [JsonProperty("Смещение Влево/Вправо (от центра X)")]
            public float OffsetX { get; set; }

            [JsonProperty("Смещение Вверх/Вниз (от центра Y)")]
            public float OffsetY { get; set; }
        }

        protected override void LoadConfig()
        {
            base.LoadConfig();
            try
            {
                _config = Config.ReadObject<Configuration>();
                if (_config == null)
                    throw new Exception();
            }
            catch
            {
                Config.WriteObject(_config, false, $"{Config.Filename}.Error.txt");
                PrintError(
                    "The configuration file contains an error and has been replaced with a default config."
                );
                LoadDefaultConfig();
            }
            SaveConfig();
        }

        protected override void LoadDefaultConfig()
        {
            _config = new Configuration();
            _config.WipeBlocks.Add(new WipeBlockItem { Name = "", ItemId = "", BlockHours = 24f });
        }

        protected override void SaveConfig() => Config.WriteObject(_config);

        #endregion


        #region Hooks & Setup

        private void Unload()
        {
            foreach (var player in BasePlayer.activePlayerList)
            {
                CuiHelper.DestroyUi(player, UIName);
                DestroyHUD(player);
            }
        }

        private void OnServerInitialized(bool serverInitialized = false)
        {
            // Красивый вывод информации о плагине в консоль
            Puts($"Загрузка плагина RSurvivalStore v{Version}");
            Puts("==================================================");
            Puts("          Plugin by RustInnovate                        ");
            Puts("--------------------------------------------------");
            Puts("  VK: vk.com/rustinnovate                    ");
            Puts("  Discord: discord.gg/e244z6aGs7                         ");
            Puts("  Telegram: t.me/RobinPlay                    ");
            Puts("==================================================");
            if (!LoadImagesToMemory())
            {
                PrintWarning(
                    "[RUS] Внимание! Отсутствуют обязательные изображения в data/RSystem/RSurvivalStore/Images/!\n"
                        + "[EN] Warning! Missing required images in data/RSystem/RSurvivalStore/Images/!"
                );
                return;
            }

            if (
                string.IsNullOrEmpty(_config.Settings.SiteID)
                || string.IsNullOrEmpty(_config.Settings.SiteKey)
            )
            {
                PrintWarning(
                    "[RUS] Плагин не настроен. Пожалуйста, используйте 'rsurvivalstore.setup <SiteID> <SiteKey>' в RCON.\n"
                        + "[EN] Plugin is not configured. Please use 'rsurvivalstore.setup <SiteID> <SiteKey>' in RCON."
                );
                return;
            }

            RegisterServer();
            timer.Every(60f, RegisterServer); // Send heartbeat every 60 seconds

            foreach (var player in BasePlayer.activePlayerList)
            {
                DrawHUD(player);
            }
        }

        private void OnPlayerSleepEnded(BasePlayer player)
        {
            DrawHUD(player);
        }

        private void OnPluginLoaded(Plugin plugin)
        {
            if (plugin.Name == "RServerMenu")
            {
                foreach (var player in BasePlayer.activePlayerList)
                    DestroyHUD(player);
            }
        }

        private void OnPluginUnloaded(Plugin plugin)
        {
            if (plugin.Name == "RServerMenu")
            {
                timer.Once(
                    0.1f,
                    () =>
                    {
                        foreach (var player in BasePlayer.activePlayerList)
                            DrawHUD(player);
                    }
                );
            }
        }

        private bool LoadImagesToMemory()
        {
            _imageCache.Clear();
            string imageDir = Interface.Oxide.DataDirectory + "/RSystem/RSurvivalStore/Images/";

            if (!Directory.Exists(imageDir))
            {
                Directory.CreateDirectory(imageDir);
            }

            string[] imagesToLoad =
            {
                _config.Design.Header.ImageName,
                _config.Design.MainBg.ImageName,
                _config.Design.ItemSlot.ImageName,
                _config.Design.ItemBlueprint.ImageName,
                _config.Design.ItemSuccess.ImageName,
                _config.Design.ItemBlocked.ImageName,
                _config.Design.ItemNoSpace.ImageName,
                _config.Design.TakeAllButton.ImageName,
                _config.Design.CartIcon.ImageName,
            };

            bool allExist = true;

            foreach (var img in imagesToLoad)
            {
                if (string.IsNullOrEmpty(img))
                    continue;

                string fileName = img.EndsWith(".png", StringComparison.OrdinalIgnoreCase)
                    ? img
                    : img + ".png";
                string path = Path.Combine(imageDir, fileName);
                if (File.Exists(path))
                {
                    byte[] data = File.ReadAllBytes(path);
                    uint id = FileStorage.server.Store(
                        data,
                        FileStorage.Type.png,
                        CommunityEntity.ServerInstance.net.ID
                    );
                    _imageCache[img] = id.ToString();
                }
                else
                {
                    PrintError(
                        $"Отсутствует необходимое изображение: data/RSystem/RSurvivalStore/Images/{fileName}"
                    );
                    allExist = false;
                }
            }

            return allExist;
        }

        [ConsoleCommand("rsurvivalstore.setup")]
        private void CmdSetup(ConsoleSystem.Arg arg)
        {
            if (arg.Connection != null)
                return;

            if (!LoadImagesToMemory())
            {
                Puts(
                    "Ошибка: Отсутствуют изображения в data/RSystem/RSurvivalStore/Images/. Загрузите их перед настройкой!"
                );
                return;
            }

            if (!arg.HasArgs(2))
            {
                Puts("Usage: rsurvivalstore.setup <SiteID> <SiteKey>");
                return;
            }

            _config.Settings.SiteID = arg.GetString(0);
            _config.Settings.SiteKey = arg.GetString(1);
            SaveConfig();

            Puts("RSurvivalStore setup complete! Registering server...");
            RegisterServer();
        }

        #endregion

        #region API Interaction

        private void SendApiRequest(
            string endpoint,
            Dictionary<string, object> data,
            Action<int, string> callback
        )
        {
            if (
                string.IsNullOrEmpty(_config.Settings.SiteID)
                || string.IsNullOrEmpty(_config.Settings.SiteKey)
            )
                return;

            string dataJson = JsonConvert.SerializeObject(data, Formatting.None);
            string salt = "salt";
            string signature;

            using (var sha256 = new SHA256Managed())
            {
                byte[] bytes = sha256.ComputeHash(
                    Encoding.UTF8.GetBytes(dataJson + _config.Settings.SiteKey + salt)
                );
                StringBuilder hex = new StringBuilder(bytes.Length * 2);
                foreach (byte b in bytes)
                    hex.AppendFormat("{0:x2}", b);
                signature = $"{hex}:{_config.Settings.SiteID}:{salt}";
            }

            data["__sign"] = signature;
            string finalJson = JsonConvert.SerializeObject(data, Formatting.None);

            webrequest.Enqueue(
                $"https://api.survivalshop.org/{endpoint}",
                finalJson,
                (code, response) =>
                {
                    callback?.Invoke(code, response);
                },
                this,
                RequestMethod.POST,
                new Dictionary<string, string> { { "Content-Type", "application/json" } }
            );
        }

        private void RegisterServer()
        {
            var data = new Dictionary<string, object>
            {
                ["siteId"] = _config.Settings.SiteID,
                ["serverData"] = new Dictionary<string, object>
                {
                    ["game"] = "RUST",
                    ["name"] = ConVar.Server.hostname,
                    ["port"] = ConVar.Server.queryport,
                    ["online"] = BasePlayer.activePlayerList.Count,
                    ["maxplayers"] = ConVar.Server.maxplayers,
                    ["pluginName"] = Name,
                    ["pluginVersion"] = Version.ToString(),
                },
            };

            SendApiRequest(
                "servers.register2",
                data,
                (code, response) =>
                {
                    if (code == 200)
                    {
                        if (!_isRegistered)
                        {
                            Puts("Successfully registered on SurvivalShop.");
                            _isRegistered = true;
                        }
                    }
                    else
                    {
                        if (_isRegistered)
                            Puts($"Lost connection to SurvivalShop or failed to heartbeat: {code}");
                        _isRegistered = false;
                    }
                }
            );
        }

        #endregion

        #region Wipe Block Logic

        private bool IsItemBlocked(string itemId, out float remainingSeconds)
        {
            remainingSeconds = 0f;
            foreach (var block in _config.WipeBlocks)
            {
                if (block.ItemId == itemId)
                {
                    float blockSeconds = block.BlockHours * 3600f;
                    float secondsSinceWipe = (float)
                        (DateTime.Now - SaveRestore.SaveCreatedTime).TotalSeconds;
                    if (secondsSinceWipe < blockSeconds)
                    {
                        remainingSeconds = blockSeconds - secondsSinceWipe;
                        return true;
                    }
                    break; // Item found, but time elapsed
                }
            }
            return false;
        }

        private string FormatTime(float seconds)
        {
            TimeSpan t = TimeSpan.FromSeconds(seconds);
            return string.Format(
                "{0:D2}ч {1:D2}м {2:D2}с",
                t.Hours + (t.Days * 24),
                t.Minutes,
                t.Seconds
            );
        }

        #endregion

        #region UI & Commands

        private const string UIName = "RSurvivalStoreUI";
        private const string HUDName = "RSurvivalStoreHUD";

        private void DrawHUD(BasePlayer player)
        {
            if (player == null || !player.IsConnected)
                return;
            var rmenu = plugins.Find("RServerMenu");
            if (rmenu != null && rmenu.IsLoaded)
                return;

            var cartCfg = _config.Design.CartIcon;
            if (
                cartCfg == null
                || string.IsNullOrEmpty(cartCfg.ImageName)
                || !_imageCache.ContainsKey(cartCfg.ImageName)
            )
                return;

            CuiHelper.DestroyUi(player, HUDName);

            CuiElementContainer container = new CuiElementContainer();
            container.Add(
                new CuiElement
                {
                    Parent = "Hud",
                    Name = HUDName,
                    Components =
                    {
                        new CuiRawImageComponent { Png = _imageCache[cartCfg.ImageName] },
                        new CuiRectTransformComponent
                        {
                            AnchorMin = "0.5 0.5",
                            AnchorMax = "0.5 0.5",
                            OffsetMin = GetOffset(
                                cartCfg.OffsetX,
                                cartCfg.OffsetY,
                                cartCfg.Width,
                                cartCfg.Height,
                                false
                            ),
                            OffsetMax = GetOffset(
                                cartCfg.OffsetX,
                                cartCfg.OffsetY,
                                cartCfg.Width,
                                cartCfg.Height,
                                true
                            ),
                        },
                    },
                }
            );

            container.Add(
                new CuiButton
                {
                    Button = { Command = "rsurvivalstore.open", Color = "0 0 0 0" },
                    Text = { Text = "" },
                    RectTransform = { AnchorMin = "0 0", AnchorMax = "1 1" },
                },
                HUDName
            );

            CuiHelper.AddUi(player, container);
        }

        private void DestroyHUD(BasePlayer player)
        {
            if (player == null)
                return;
            CuiHelper.DestroyUi(player, HUDName);
        }

        [ChatCommand("store")]
        private void CmdShopChat(BasePlayer player, string command, string[] args)
        {
            if (_config.Settings.BlockInEnemyTC && player.IsBuildingBlocked())
            {
                ShowGameTip(player, Lang("BlockedInTC", player.UserIDString), 2);
                return;
            }

            if (_config.Settings.BlockInRaid && IsPlayerRaidBlocked(player))
            {
                ShowGameTip(player, Lang("BlockedInRaid", player.UserIDString), 2);
                return;
            }

            if (_config.Settings.BlockInCombat && IsPlayerCombatBlocked(player))
            {
                ShowGameTip(player, Lang("BlockedInCombat", player.UserIDString), 2);
                return;
            }

            var rmenu = plugins.Find("RServerMenu");
            if (rmenu != null && rmenu.IsLoaded)
            {
                rmenu.Call("OpenMenuCategory", player, "cart");
                return;
            }
            _activeAddonParent.Remove(player.userID);
            OpenShopUI(player);
        }

        [HookMethod("OpenAddonUI")]
        public void OpenAddonUI(BasePlayer player, string parentName)
        {
            if (_config.Settings.BlockInEnemyTC && player.IsBuildingBlocked())
            {
                ShowGameTip(player, Lang("BlockedInTC", player.UserIDString), 2);
                return;
            }

            if (_config.Settings.BlockInRaid && IsPlayerRaidBlocked(player))
            {
                ShowGameTip(player, Lang("BlockedInRaid", player.UserIDString), 2);
                return;
            }

            if (_config.Settings.BlockInCombat && IsPlayerCombatBlocked(player))
            {
                ShowGameTip(player, Lang("BlockedInCombat", player.UserIDString), 2);
                return;
            }

            _activeAddonParent[player.userID] = parentName;
            OpenShopUI(player);
        }

        private bool IsPlayerRaidBlocked(BasePlayer player)
        {
            var raidBlock = plugins.Find("RaidBlock");
            if (raidBlock != null && raidBlock.IsLoaded)
            {
                object result = raidBlock.Call("IsBlocked", player);
                if (result is bool && (bool)result)
                    return true;

                result = raidBlock.Call("IsRaidBlocked", player);
                if (result is bool && (bool)result)
                    return true;
            }

            var noEscape = plugins.Find("NoEscape");
            if (noEscape != null && noEscape.IsLoaded)
            {
                object result = noEscape.Call("IsRaidBlocked", player);
                if (result is bool && (bool)result)
                    return true;
            }

            return false;
        }

        private bool IsPlayerCombatBlocked(BasePlayer player)
        {
            var noEscape = plugins.Find("NoEscape");
            if (noEscape != null && noEscape.IsLoaded)
            {
                object result = noEscape.Call("IsCombatBlocked", player);
                if (result is bool && (bool)result)
                    return true;
            }

            var combatBlock = plugins.Find("CombatBlock");
            if (combatBlock != null && combatBlock.IsLoaded)
            {
                object result = combatBlock.Call("IsCombatBlocked", player);
                if (result is bool && (bool)result)
                    return true;

                result = combatBlock.Call("IsCombatBlock", player);
                if (result is bool && (bool)result)
                    return true;
            }

            return false;
        }

        [ConsoleCommand("rsurvivalstore.open")]
        private void CmdOpenUI(ConsoleSystem.Arg arg)
        {
            if (arg.Player() != null)
            {
                CmdShopChat(arg.Player(), "", null);
            }
        }

        private void ShowGameTip(BasePlayer player, string message, int type = 2)
        {
            if (player?.IsConnected != true)
                return;
            player.SendConsoleCommand("gametip.showtoast", type, message);
        }

        [ConsoleCommand("rsurvivalstore.close")]
        private void CmdCloseUI(ConsoleSystem.Arg arg)
        {
            if (arg.Player() != null)
            {
                CuiHelper.DestroyUi(arg.Player(), UIName);
                _activeAddonParent.Remove(arg.Player().userID);
            }
        }

        private void OpenShopUI(BasePlayer player)
        {
            if (!_isRegistered)
            {
                Player.Message(player, Lang("NotConfigured", player.UserIDString));
                return;
            }

            if (_imageCache.Count < 4)
            {
                Player.Message(
                    player,
                    "Магазин временно недоступен (отсутствуют изображения интерфейса)."
                );
                return;
            }

            var data = new Dictionary<string, object>
            {
                ["siteId"] = _config.Settings.SiteID,
                ["clientSid"] = player.UserIDString,
                ["criteria"] = new Dictionary<string, object>
                {
                    ["_start"] = 0,
                    ["_limit"] = 100, // Fetch up to 100 items for the scroll view
                },
            };

            SendApiRequest(
                "client.getInventory",
                data,
                (code, response) =>
                {
                    if (code != 200 || string.IsNullOrEmpty(response))
                    {
                        DrawUI(player, null, true);
                        return;
                    }

                    try
                    {
                        // DEBUG LOG FOR API RESPONSE
                        // Puts($"[DEBUG] getInventory response for {player.UserIDString}: {response}");

                        JObject json = JObject.Parse(response);

                        string errorMsg = json["error_msg"]?.ToString();
                        if (errorMsg == null && json["error_code"] != null)
                            errorMsg = "Unknown Error";

                        if (!string.IsNullOrEmpty(errorMsg))
                        {
                            if (
                                errorMsg.IndexOf(
                                    "Mesh not exist",
                                    StringComparison.OrdinalIgnoreCase
                                ) >= 0
                                || errorMsg.IndexOf(
                                    "Клиент не найден",
                                    StringComparison.OrdinalIgnoreCase
                                ) >= 0
                            )
                            {
                                DrawUI(player, null, true);
                                return;
                            }

                            Player.Message(player, Lang("ApiError", player.UserIDString));
                            PrintError(
                                $"[RSurvivalStore] API Error for {player.UserIDString}: {errorMsg}"
                            );
                            return;
                        }

                        JArray items = (JArray)json["response"]?["result"];
                        CacheItemImages(
                            player,
                            items,
                            () =>
                            {
                                DrawUI(player, items, false);
                            }
                        );
                    }
                    catch (Exception ex)
                    {
                        PrintError($"Failed to parse shop data: {ex.Message}");
                    }
                }
            );
        }

        private void CacheItemImages(BasePlayer player, JArray items, Action onComplete)
        {
            if (items == null || items.Count == 0)
            {
                onComplete?.Invoke();
                return;
            }

            string cacheDir = Path.Combine(
                Interface.Oxide.DataDirectory,
                "RSystem",
                "RSurvivalStore",
                "ImageCache"
            );
            if (!Directory.Exists(cacheDir))
                Directory.CreateDirectory(cacheDir);

            Dictionary<string, string> pendingDownloads = new Dictionary<string, string>();

            foreach (JObject item in items)
            {
                string logoLink = (string)item["logoLink"];
                if (string.IsNullOrEmpty(logoLink))
                    continue;

                if (_imageCache.ContainsKey(logoLink))
                    continue;

                string imageUrl = logoLink;
                if (logoLink.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
                    imageUrl = "https://" + logoLink.Substring(7);
                else if (!logoLink.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                    imageUrl = "https://survivalshop.org" + logoLink;

                string fileName = logoLink.GetHashCode().ToString() + ".png";
                string localPath = Path.Combine(cacheDir, fileName);

                if (File.Exists(localPath))
                {
                    try
                    {
                        byte[] data = File.ReadAllBytes(localPath);
                        uint id = FileStorage.server.Store(
                            data,
                            FileStorage.Type.png,
                            CommunityEntity.ServerInstance.net.ID
                        );
                        _imageCache[logoLink] = id.ToString();
                    }
                    catch (Exception ex)
                    {
                        PrintError($"Failed to load cached image {fileName}: {ex.Message}");
                        if (!pendingDownloads.ContainsKey(logoLink))
                            pendingDownloads.Add(logoLink, imageUrl);
                    }
                }
                else
                {
                    if (!pendingDownloads.ContainsKey(logoLink))
                        pendingDownloads.Add(logoLink, imageUrl);
                }
            }

            if (pendingDownloads.Count == 0)
            {
                onComplete?.Invoke();
                return;
            }

            Player.Message(
                player,
                "Кэширование изображений товаров, пожалуйста подождите несколько секунд..."
            );

            int total = pendingDownloads.Count;
            int completed = 0;
            Action itemCompleted = () =>
            {
                completed++;
                if (completed >= total)
                    onComplete?.Invoke();
            };

            foreach (var kvp in pendingDownloads)
            {
                string logoLink = kvp.Key;
                string imageUrl = kvp.Value;
                string fileName = logoLink.GetHashCode().ToString() + ".png";
                string localPath = Path.Combine(cacheDir, fileName);

                ServerMgr.Instance.StartCoroutine(
                    DownloadImageCoroutine(imageUrl, localPath, logoLink, itemCompleted)
                );
            }
        }

        private System.Collections.IEnumerator DownloadImageCoroutine(
            string url,
            string localPath,
            string logoLink,
            Action onComplete
        )
        {
            using (var www = UnityEngine.Networking.UnityWebRequest.Get(url))
            {
                yield return www.SendWebRequest();
                if (www.isNetworkError || www.isHttpError)
                {
                    PrintError($"Failed to download image {url}: {www.error}");
                }
                else
                {
                    byte[] bytes = www.downloadHandler.data;
                    if (bytes != null && bytes.Length > 0)
                    {
                        try
                        {
                            System.IO.File.WriteAllBytes(localPath, bytes);
                            uint id = FileStorage.server.Store(
                                bytes,
                                FileStorage.Type.png,
                                CommunityEntity.ServerInstance.net.ID
                            );
                            _imageCache[logoLink] = id.ToString();
                        }
                        catch (Exception ex)
                        {
                            PrintError($"Failed to save downloaded image {logoLink}: {ex.Message}");
                        }
                    }
                }
                onComplete?.Invoke();
            }
        }

        private string GetOffset(
            float offsetX,
            float offsetY,
            float width,
            float height,
            bool isMax
        )
        {
            float halfW = width / 2f;
            float halfH = height / 2f;

            if (isMax)
            {
                return $"{offsetX + halfW} {offsetY + halfH}";
            }
            else
            {
                return $"{offsetX - halfW} {offsetY - halfH}";
            }
        }

        private void DrawSingleItemSlot(BasePlayer player, string targetSlotId, bool remove)
        {
            if (!_playerItems.TryGetValue(player.UserIDString, out JArray items))
                return;

            int index = -1;
            for (int i = 0; i < items.Count; i++)
            {
                if ((string)items[i]["_id"] == targetSlotId)
                {
                    index = i;
                    break;
                }
            }

            if (index == -1)
                return;

            string contentName = UIName + "_Content";
            string itemPanel = $"{UIName}_Item_{index}";

            CuiHelper.DestroyUi(player, itemPanel);

            if (remove)
                return;

            var slotCfg = _config.Design.ItemSlot;
            var successCfg = _config.Design.ItemSuccess;
            var mainCfg = _config.Design.MainBg;

            float gapX = 15f;
            float gapY = 40f;
            int columns = UnityEngine.Mathf.FloorToInt(
                (mainCfg.Width - 20f) / (slotCfg.Width + gapX)
            );
            if (columns < 1)
                columns = 1;

            float totalGridWidth = (columns * slotCfg.Width) + ((columns - 1) * gapX);
            float startX = -(totalGridWidth / 2f) + (slotCfg.Width / 2f);
            float startY = -(slotCfg.Height / 2f) - 30f;

            JObject item = (JObject)items[index];
            string slotId = (string)item["_id"];
            string itemId = (string)item["meta"]?["_item"] ?? slotId;
            string title = (string)item["title"];
            int count = (int)item["count"];
            string logoLink = (string)item["logoLink"];

            bool isBlueprint =
                title != null
                && (
                    title.IndexOf("чертеж", StringComparison.OrdinalIgnoreCase) >= 0
                    || title.IndexOf("чертёж", StringComparison.OrdinalIgnoreCase) >= 0
                    || title.IndexOf("blueprint", StringComparison.OrdinalIgnoreCase) >= 0
                    || title.IndexOf("[ч]", StringComparison.OrdinalIgnoreCase) >= 0
                    || title.IndexOf("[b]", StringComparison.OrdinalIgnoreCase) >= 0
                );

            bool hasState = _slotStates.TryGetValue(slotId, out string slotState);
            bool isNoSpace = hasState && slotState.StartsWith("nospace");
            var activeCfg = slotCfg;
            string bgImageName = isBlueprint
                ? _config.Design.ItemBlueprint.ImageName
                : slotCfg.ImageName;

            if (hasState)
            {
                if (slotState == "success")
                {
                    activeCfg = successCfg;
                    bgImageName = successCfg.ImageName;
                }
                else if (isNoSpace)
                {
                    activeCfg = _config.Design.ItemNoSpace;
                    bgImageName = _config.Design.ItemNoSpace.ImageName;
                }
            }

            int row = index / columns;
            int col = index % columns;
            float currentX = startX + (col * (slotCfg.Width + gapX));
            float currentY = startY - (row * (slotCfg.Height + gapY));

            CuiElementContainer container = new CuiElementContainer();

            container.Add(
                new CuiElement
                {
                    Parent = contentName,
                    Name = itemPanel,
                    Components =
                    {
                        new CuiRawImageComponent
                        {
                            Png = _imageCache.ContainsKey(bgImageName)
                                ? _imageCache[bgImageName]
                                : "",
                        },
                        new CuiRectTransformComponent
                        {
                            AnchorMin = "0.5 1",
                            AnchorMax = "0.5 1",
                            OffsetMin = GetOffset(
                                activeCfg.OffsetX + currentX,
                                activeCfg.OffsetY + currentY,
                                activeCfg.Width,
                                activeCfg.Height,
                                false
                            ),
                            OffsetMax = GetOffset(
                                activeCfg.OffsetX + currentX,
                                activeCfg.OffsetY + currentY,
                                activeCfg.Width,
                                activeCfg.Height,
                                true
                            ),
                        },
                    },
                }
            );

            container.Add(
                new CuiElement
                {
                    Parent = itemPanel,
                    Name = itemPanel + "_Icon",
                    Components =
                    {
                        new CuiRawImageComponent
                        {
                            Png = _imageCache.ContainsKey(logoLink) ? _imageCache[logoLink] : "",
                        },
                        new CuiRectTransformComponent
                        {
                            AnchorMin = "0.225 0.15",
                            AnchorMax = "0.775 0.7",
                        },
                    },
                }
            );

            container.Add(
                new CuiLabel
                {
                    Text =
                    {
                        Text = title,
                        FontSize = 11,
                        Align = TextAnchor.LowerCenter,
                        Color = "1 1 1 1",
                        Font = "robotocondensed-bold.ttf",
                    },
                    RectTransform =
                    {
                        AnchorMin = "-0.05 1.02",
                        AnchorMax = "1.05 1.35",
                        OffsetMin = "0 0",
                        OffsetMax = "0 0",
                    },
                },
                itemPanel
            );

            container.Add(
                new CuiLabel
                {
                    Text =
                    {
                        Text = $"x{count}",
                        FontSize = 12,
                        Align = TextAnchor.LowerRight,
                        Color = "1 1 1 1",
                        Font = "robotocondensed-bold.ttf",
                    },
                    RectTransform =
                    {
                        AnchorMin = "0 0.05",
                        AnchorMax = "0.92 0.25",
                        OffsetMin = "0 0",
                        OffsetMax = "0 0",
                    },
                },
                itemPanel
            );

            if (slotState == "success")
            {
                container.Add(
                    new CuiPanel
                    {
                        Image = { Color = "0 0 0 0.75" },
                        RectTransform = { AnchorMin = "0 0", AnchorMax = "1 1" },
                    },
                    itemPanel,
                    $"{itemPanel}_Dim"
                );
                container.Add(
                    new CuiLabel
                    {
                        Text =
                        {
                            Text = Lang("Taken", player.UserIDString),
                            FontSize = 14,
                            Align = TextAnchor.MiddleCenter,
                            Color = "0.4 0.9 0.4 1",
                            Font = "robotocondensed-bold.ttf",
                        },
                        RectTransform = { AnchorMin = "0 0.7", AnchorMax = "1 0.98" },
                    },
                    $"{itemPanel}_Dim"
                );
            }
            else if (isNoSpace)
            {
                string countNeeded = slotState.Split(':')[1];
                container.Add(
                    new CuiPanel
                    {
                        Image = { Color = "0 0 0 0" },
                        RectTransform = { AnchorMin = "0 0", AnchorMax = "1 1" },
                    },
                    itemPanel,
                    $"{itemPanel}_Dim"
                );
                container.Add(
                    new CuiLabel
                    {
                        Text =
                        {
                            Text = string.Format(
                                Lang("NeedSlots", player.UserIDString),
                                countNeeded
                            ),
                            FontSize = 12,
                            Align = TextAnchor.MiddleCenter,
                            Color = "1 1 1 1",
                            Font = "robotocondensed-bold.ttf",
                        },
                        RectTransform = { AnchorMin = "0 0.7", AnchorMax = "1 0.98" },
                    },
                    $"{itemPanel}_Dim"
                );
            }
            container.Add(
                new CuiButton
                {
                    Button =
                    {
                        Color = "0 0 0 0",
                        Command = $"rsurvivalstore.take {slotId} {itemId}",
                    },
                    Text = { Text = "" },
                    RectTransform = { AnchorMin = "0 0", AnchorMax = "1 1" },
                },
                itemPanel
            );

            CuiHelper.AddUi(player, container);
        }

        private void DrawUI(BasePlayer player, JArray items, bool isAuthError = false)
        {
            if (items != null)
                _playerItems[player.UserIDString] = items;

            _activeAddonParent.TryGetValue(player.userID, out string parentName);
            bool isAddon = !string.IsNullOrEmpty(parentName);

            if (!isAddon)
                CuiHelper.DestroyUi(player, UIName);

            CuiElementContainer container = new CuiElementContainer();

            if (!isAddon)
            {
                // Invisible Background to block cursor
                container.Add(
                    new CuiPanel
                    {
                        Image =
                        {
                            Color = "0 0 0 0.5",
                            Material = "assets/content/ui/uibackgroundblur.mat",
                        },
                        RectTransform = { AnchorMin = "0 0", AnchorMax = "1 1" },
                        CursorEnabled = true,
                    },
                    "Overlay",
                    UIName
                );

                // Background Button (Closes UI when clicked)
                container.Add(
                    new CuiButton
                    {
                        Button = { Color = "0 0 0 0", Command = "rsurvivalstore.close" },
                        Text = { Text = "" },
                        RectTransform = { AnchorMin = "0 0", AnchorMax = "1 1" },
                    },
                    UIName
                );
            }

            // --- Main Background ---
            var mainCfg = _config.Design.MainBg;
            string mainPanel = UIName + "_Main";
            string actualParent = isAddon ? parentName : UIName;

            container.Add(
                new CuiElement
                {
                    Parent = actualParent,
                    Name = mainPanel,
                    Components =
                    {
                        isAddon
                            ? new CuiImageComponent { Color = "0 0 0 0" }
                            : new CuiRawImageComponent { Png = _imageCache[mainCfg.ImageName] }
                                as ICuiComponent,
                        new CuiRectTransformComponent
                        {
                            AnchorMin = isAddon ? "0 0" : "0.5 0.5",
                            AnchorMax = isAddon ? "1 1" : "0.5 0.5",
                            OffsetMin = isAddon
                                ? "0 0"
                                : GetOffset(
                                    mainCfg.OffsetX,
                                    mainCfg.OffsetY,
                                    mainCfg.Width,
                                    mainCfg.Height,
                                    false
                                ),
                            OffsetMax = isAddon
                                ? "0 0"
                                : GetOffset(
                                    mainCfg.OffsetX,
                                    mainCfg.OffsetY,
                                    mainCfg.Width,
                                    mainCfg.Height,
                                    true
                                ),
                        },
                    },
                }
            );

            // --- Header Background ---
            if (!isAddon)
            {
                var headerCfg = _config.Design.Header;
                string headerPanel = UIName + "_Header";

                container.Add(
                    new CuiElement
                    {
                        Parent = actualParent,
                        Name = headerPanel,
                        Components =
                        {
                            new CuiRawImageComponent { Png = _imageCache[headerCfg.ImageName] },
                            new CuiRectTransformComponent
                            {
                                AnchorMin = "0.5 0.5",
                                AnchorMax = "0.5 0.5",
                                OffsetMin = GetOffset(
                                    headerCfg.OffsetX,
                                    headerCfg.OffsetY,
                                    headerCfg.Width,
                                    headerCfg.Height,
                                    false
                                ),
                                OffsetMax = GetOffset(
                                    headerCfg.OffsetX,
                                    headerCfg.OffsetY,
                                    headerCfg.Width,
                                    headerCfg.Height,
                                    true
                                ),
                            },
                        },
                    }
                );

                // Title Text inside Header
                container.Add(
                    new CuiLabel
                    {
                        Text =
                        {
                            Text = Lang("Title", player.UserIDString),
                            FontSize = 24,
                            Align = TextAnchor.MiddleCenter,
                            Font = "robotocondensed-bold.ttf",
                            Color = "1 1 1 1",
                        },
                        RectTransform = { AnchorMin = "0 0", AnchorMax = "1 1" },
                    },
                    headerPanel
                );
            }

            // Take All Button
            var takeAllCfg = _config.Design.TakeAllButton;
            int validItemsCount = 0;
            if (items != null)
            {
                foreach (JToken t in items)
                {
                    if (t is JObject obj && (int)(obj["count"] ?? 0) > 0)
                        validItemsCount++;
                }
            }

            if (
                takeAllCfg != null
                && !string.IsNullOrEmpty(takeAllCfg.ImageName)
                && _imageCache.ContainsKey(takeAllCfg.ImageName)
                && validItemsCount > 1
            )
            {
                string takeAllPanel = UIName + "_TakeAll";
                string takeAllParent = isAddon ? "RMenu.Content" : actualParent;

                container.Add(
                    new CuiElement
                    {
                        Parent = takeAllParent,
                        Name = takeAllPanel,
                        Components =
                        {
                            new CuiRawImageComponent { Png = _imageCache[takeAllCfg.ImageName] },
                            new CuiRectTransformComponent
                            {
                                AnchorMin = isAddon ? "0.91 0.942" : "0.5 0.5",
                                AnchorMax = isAddon ? "0.91 0.942" : "0.5 0.5",
                                OffsetMin = isAddon
                                    ? "-14 -14"
                                    : GetOffset(
                                        takeAllCfg.OffsetX,
                                        takeAllCfg.OffsetY,
                                        takeAllCfg.Width,
                                        takeAllCfg.Height,
                                        false
                                    ),
                                OffsetMax = isAddon
                                    ? "14 14"
                                    : GetOffset(
                                        takeAllCfg.OffsetX,
                                        takeAllCfg.OffsetY,
                                        takeAllCfg.Width,
                                        takeAllCfg.Height,
                                        true
                                    ),
                            },
                        },
                    }
                );

                container.Add(
                    new CuiButton
                    {
                        Button = { Command = "rsurvivalstore.takeall", Color = "0 0 0 0" },
                        Text = { Text = "" },
                        RectTransform = { AnchorMin = "0 0", AnchorMax = "1 1" },
                    },
                    takeAllPanel
                );
            }

            // --- Content ---
            // --- Content ---
            if (isAuthError)
            {
                string emptyPanel = UIName + "_Empty";
                container.Add(
                    new CuiElement
                    {
                        Parent = mainPanel,
                        Name = emptyPanel,
                        Components =
                        {
                            new CuiRectTransformComponent { AnchorMin = "0 0", AnchorMax = "1 1" },
                        },
                    }
                );

                container.Add(
                    new CuiLabel
                    {
                        Text =
                        {
                            Text = Lang("NotAuthorized", player.UserIDString),
                            FontSize = 18,
                            Align = TextAnchor.LowerCenter,
                            Color = "0.9 0.4 0.4 1",
                        },
                        RectTransform = { AnchorMin = "0 0.55", AnchorMax = "1 1" },
                    },
                    emptyPanel
                );

                container.Add(
                    new CuiElement
                    {
                        Parent = emptyPanel,
                        Components =
                        {
                            new CuiInputFieldComponent
                            {
                                Text = _config.Settings.ShopURL,
                                FontSize = 18,
                                Align = TextAnchor.UpperCenter,
                                Color = "1 0.66 0 1",
                                ReadOnly = true,
                            },
                            new CuiRectTransformComponent
                            {
                                AnchorMin = "0 0",
                                AnchorMax = "1 0.5",
                            },
                        },
                    }
                );
            }
            else if (items == null || items.Count == 0)
            {
                container.Add(
                    new CuiLabel
                    {
                        Text =
                        {
                            Text = Lang("Empty", player.UserIDString),
                            FontSize = 18,
                            Align = TextAnchor.MiddleCenter,
                            Color = "0.8 0.8 0.8 1",
                        },
                        RectTransform = { AnchorMin = "0 0", AnchorMax = "1 1" },
                    },
                    mainPanel
                );
            }
            else
            {
                var slotCfg = _config.Design.ItemSlot;
                var successCfg = _config.Design.ItemSuccess;

                string contentName = UIName + "_Content";

                float gapX = 15f;
                float gapY = 40f;
                int columns = UnityEngine.Mathf.FloorToInt(
                    (mainCfg.Width - 20f) / (slotCfg.Width + gapX)
                );
                if (columns < 1)
                    columns = 1;

                float totalGridWidth = (columns * slotCfg.Width) + ((columns - 1) * gapX);
                float startX = -(totalGridWidth / 2f) + (slotCfg.Width / 2f);
                float startY = -(slotCfg.Height / 2f) - 30f;

                int totalRows = UnityEngine.Mathf.CeilToInt((float)items.Count / columns);
                float totalContentHeight = totalRows * (slotCfg.Height + gapY) + 40f;
                float viewportHeight = mainCfg.Height * 0.9f;

                if (totalContentHeight < viewportHeight)
                    totalContentHeight = viewportHeight;

                // Native ScrollView
                container.Add(
                    new CuiElement
                    {
                        Parent = mainPanel,
                        Name = contentName,
                        Components =
                        {
                            new CuiImageComponent { Color = "0 0 0 0" },
                            new Oxide.Game.Rust.Cui.CuiScrollViewComponent
                            {
                                ContentTransform = new CuiRectTransformComponent
                                {
                                    AnchorMin = "0 1",
                                    AnchorMax = "1 1",
                                    OffsetMin = $"0 -{totalContentHeight}",
                                    OffsetMax = "0 0",
                                },
                                Vertical = true,
                                Horizontal = false,
                                Inertia = true,
                                ScrollSensitivity = 30f,
                            },
                            new CuiRectTransformComponent
                            {
                                AnchorMin = "0.02 0.05",
                                AnchorMax = "0.98 0.95",
                            },
                        },
                    }
                );

                for (int i = 0; i < items.Count; i++)
                {
                    JObject item = (JObject)items[i];
                    string slotId = (string)item["_id"];
                    string itemId = (string)item["meta"]?["_item"] ?? slotId;
                    string title = (string)item["title"];
                    int count = (int)item["count"];
                    string logoLink = (string)item["logoLink"];

                    bool isBlueprint =
                        title != null
                        && (
                            title.IndexOf("чертеж", StringComparison.OrdinalIgnoreCase) >= 0
                            || title.IndexOf("чертёж", StringComparison.OrdinalIgnoreCase) >= 0
                            || title.IndexOf("blueprint", StringComparison.OrdinalIgnoreCase) >= 0
                            || title.IndexOf("[ч]", StringComparison.OrdinalIgnoreCase) >= 0
                            || title.IndexOf("[b]", StringComparison.OrdinalIgnoreCase) >= 0
                        );

                    bool hasState = _slotStates.TryGetValue(slotId, out string slotState);
                    bool isNoSpace = hasState && slotState.StartsWith("nospace");
                    var activeCfg = slotCfg;
                    string bgImageName = isBlueprint
                        ? _config.Design.ItemBlueprint.ImageName
                        : slotCfg.ImageName;

                    if (hasState)
                    {
                        if (slotState == "success")
                        {
                            activeCfg = successCfg;
                            bgImageName = successCfg.ImageName;
                        }
                        else if (isNoSpace)
                        {
                            activeCfg = _config.Design.ItemNoSpace;
                            bgImageName = _config.Design.ItemNoSpace.ImageName;
                        }
                    }

                    string itemPanel = $"{UIName}_Item_{i}";

                    int row = i / columns;
                    int col = i % columns;
                    float currentX = startX + (col * (slotCfg.Width + gapX));
                    float currentY = startY - (row * (slotCfg.Height + gapY));

                    container.Add(
                        new CuiElement
                        {
                            Parent = contentName,
                            Name = itemPanel,
                            Components =
                            {
                                new CuiRawImageComponent
                                {
                                    Png = _imageCache.ContainsKey(bgImageName)
                                        ? _imageCache[bgImageName]
                                        : "",
                                },
                                new CuiRectTransformComponent
                                {
                                    AnchorMin = "0.5 1",
                                    AnchorMax = "0.5 1",
                                    OffsetMin = GetOffset(
                                        activeCfg.OffsetX + currentX,
                                        activeCfg.OffsetY + currentY,
                                        activeCfg.Width,
                                        activeCfg.Height,
                                        false
                                    ),
                                    OffsetMax = GetOffset(
                                        activeCfg.OffsetX + currentX,
                                        activeCfg.OffsetY + currentY,
                                        activeCfg.Width,
                                        activeCfg.Height,
                                        true
                                    ),
                                },
                            },
                        }
                    );

                    // Add Image of the item
                    if (!string.IsNullOrEmpty(logoLink))
                    {
                        container.Add(
                            new CuiElement
                            {
                                Parent = itemPanel,
                                Name = $"{itemPanel}_Icon",
                                Components =
                                {
                                    new CuiRawImageComponent
                                    {
                                        Png = _imageCache.ContainsKey(logoLink)
                                            ? _imageCache[logoLink]
                                            : "",
                                    },
                                    new CuiRectTransformComponent
                                    {
                                        AnchorMin = "0.225 0.15",
                                        AnchorMax = "0.775 0.7",
                                    },
                                },
                            }
                        );
                    }

                    // Add Title at the top
                    container.Add(
                        new CuiLabel
                        {
                            Text =
                            {
                                Text = title,
                                FontSize = 11,
                                Align = TextAnchor.LowerCenter,
                                Color = "1 1 1 1",
                                Font = "robotocondensed-bold.ttf",
                            },
                            RectTransform = { AnchorMin = "-0.05 1.02", AnchorMax = "1.05 1.35" },
                        },
                        itemPanel
                    );

                    // Add Count at the bottom right
                    container.Add(
                        new CuiLabel
                        {
                            Text =
                            {
                                Text = $"x{count}",
                                FontSize = 12,
                                Align = TextAnchor.LowerRight,
                                Color = "1 1 1 1",
                                Font = "robotocondensed-bold.ttf",
                            },
                            RectTransform = { AnchorMin = "0 0.05", AnchorMax = "0.92 0.25" },
                        },
                        itemPanel
                    );

                    if (slotState == "success")
                    {
                        container.Add(
                            new CuiPanel
                            {
                                Image = { Color = "0 0 0 0.75" },
                                RectTransform = { AnchorMin = "0 0", AnchorMax = "1 1" },
                            },
                            itemPanel,
                            $"{itemPanel}_Dim"
                        );
                        container.Add(
                            new CuiLabel
                            {
                                Text =
                                {
                                    Text = Lang("Taken", player.UserIDString),
                                    FontSize = 14,
                                    Align = TextAnchor.MiddleCenter,
                                    Color = "0.4 0.9 0.4 1",
                                    Font = "robotocondensed-bold.ttf",
                                },
                                RectTransform = { AnchorMin = "0 0.8", AnchorMax = "1 1" },
                            },
                            $"{itemPanel}_Dim"
                        );
                    }
                    else if (isNoSpace)
                    {
                        string countNeeded = slotState.Split(':')[1];
                        container.Add(
                            new CuiPanel
                            {
                                Image = { Color = "0 0 0 0" },
                                RectTransform = { AnchorMin = "0 0", AnchorMax = "1 1" },
                            },
                            itemPanel,
                            $"{itemPanel}_Dim"
                        );
                        container.Add(
                            new CuiLabel
                            {
                                Text =
                                {
                                    Text = string.Format(
                                        Lang("NeedSlots", player.UserIDString),
                                        countNeeded
                                    ),
                                    FontSize = 12,
                                    Align = TextAnchor.MiddleCenter,
                                    Color = "1 1 1 1",
                                    Font = "robotocondensed-bold.ttf",
                                },
                                RectTransform = { AnchorMin = "0 0.8", AnchorMax = "1 1" },
                            },
                            $"{itemPanel}_Dim"
                        );
                    }
                    else if (IsItemBlocked(itemId, out float remaining))
                    {
                        var blockCfg = _config.Design.ItemBlocked;
                        container.Add(
                            new CuiElement
                            {
                                Parent = itemPanel,
                                Name = $"{itemPanel}_Dim",
                                Components =
                                {
                                    new CuiRawImageComponent
                                    {
                                        Png = _imageCache[blockCfg.ImageName],
                                    },
                                    new CuiRectTransformComponent
                                    {
                                        AnchorMin = "0.5 0.5",
                                        AnchorMax = "0.5 0.5",
                                        OffsetMin =
                                            $"{blockCfg.OffsetX - blockCfg.Width / 2f} {blockCfg.OffsetY - blockCfg.Height / 2f}",
                                        OffsetMax =
                                            $"{blockCfg.OffsetX + blockCfg.Width / 2f} {blockCfg.OffsetY + blockCfg.Height / 2f}",
                                    },
                                },
                            }
                        );
                        container.Add(
                            new CuiElement
                            {
                                Parent = $"{itemPanel}_Dim",
                                Components =
                                {
                                    new CuiTextComponent
                                    {
                                        Text = "%TIME_LEFT%",
                                        FontSize = 14,
                                        Align = TextAnchor.MiddleCenter,
                                        Color = "1 1 1 1",
                                        Font = "robotocondensed-bold.ttf",
                                    },
                                    new CuiCountdownComponent
                                    {
                                        StartTime = (int)remaining,
                                        EndTime = 0,
                                        Step = 1,
                                        TimerFormat = Oxide
                                            .Game
                                            .Rust
                                            .Cui
                                            .TimerFormat
                                            .DaysHoursMinutesSeconds,
                                    },
                                    new CuiRectTransformComponent
                                    {
                                        AnchorMin = "0 0",
                                        AnchorMax = "1 1",
                                    },
                                },
                            }
                        );
                    }
                    else
                    {
                        // Invisible Full-cell Button to Take
                        container.Add(
                            new CuiButton
                            {
                                Button =
                                {
                                    Color = "0 0 0 0",
                                    Command = $"rsurvivalstore.take {slotId} {itemId}",
                                },
                                Text = { Text = "" },
                                RectTransform = { AnchorMin = "0 0", AnchorMax = "1 1" },
                            },
                            itemPanel
                        );
                    }
                }
            }

            CuiHelper.AddUi(player, container);
        }

        #endregion

        #region Take Item Logic

        [ConsoleCommand("rsurvivalstore.takeall")]
        private void CmdTakeAll(ConsoleSystem.Arg arg)
        {
            BasePlayer player = arg.Player();
            if (player == null)
                return;

            if (!_playerItems.TryGetValue(player.UserIDString, out JArray items))
            {
                return;
            }

            int freeSlots =
                player.inventory.containerMain.capacity
                - player.inventory.containerMain.itemList.Count;
            freeSlots +=
                player.inventory.containerBelt.capacity
                - player.inventory.containerBelt.itemList.Count;

            float takeDelay = 0f;

            foreach (JToken token in items)
            {
                JObject item = token as JObject;
                if (item == null)
                    continue;

                string slotId = (string)item["_id"];
                string itemId =
                    (string)item["meta"]?["_item"] != null ? (string)item["meta"]["_item"] : slotId;
                if (string.IsNullOrEmpty(slotId))
                    continue;

                if (IsItemBlocked(itemId, out float remaining))
                    continue;

                int maxCount = (int)(item["count"] ?? 0);
                if (maxCount <= 0)
                    continue;

                string title = (string)item["title"] ?? "";
                bool isBackpack =
                    title.IndexOf("[backpack]", StringComparison.OrdinalIgnoreCase) >= 0
                    || title.IndexOf("[рюкзак]", StringComparison.OrdinalIgnoreCase) >= 0;

                int quantityToTake = 1;
                if (
                    title.IndexOf("[superstack]", StringComparison.OrdinalIgnoreCase) >= 0
                    || title.IndexOf("[суперстак]", StringComparison.OrdinalIgnoreCase) >= 0
                    || title.IndexOf("[++]", StringComparison.OrdinalIgnoreCase) >= 0
                )
                {
                    quantityToTake = maxCount;
                }

                if (!isBackpack)
                {
                    int requiredSlots = CalculateRequiredSlots(item, quantityToTake);
                    if (freeSlots < requiredSlots)
                    {
                        // Simulate CmdTake visual feedback for items that do not fit
                        _slotStates[slotId] = $"nospace:{requiredSlots - freeSlots}";
                        DrawSingleItemSlot(player, slotId, false);

                        timer.Once(
                            1.5f,
                            () =>
                            {
                                _slotStates.Remove(slotId);
                                DrawSingleItemSlot(player, slotId, false); // Re-draw back to normal state
                            }
                        );
                        continue;
                    }
                    freeSlots -= requiredSlots;
                }

                float currentDelay = takeDelay;
                string captureSlot = slotId;
                string captureItem = itemId;

                timer.Once(
                    currentDelay,
                    () =>
                    {
                        if (player != null && player.IsConnected)
                        {
                            player.SendConsoleCommand(
                                $"rsurvivalstore.take \"{captureSlot}\" \"{captureItem}\""
                            );
                        }
                    }
                );

                takeDelay += 0.5f; // Add a 0.5s delay between each item to prevent backend database race condition (duping)
            }
        }

        [ConsoleCommand("rsurvivalstore.take")]
        private void CmdTake(ConsoleSystem.Arg arg)
        {
            BasePlayer player = arg.Player();
            if (player == null || !arg.HasArgs(2))
                return;

            string slotId = arg.GetString(0);
            string itemId = arg.GetString(1);

            if (IsItemBlocked(itemId, out float remaining))
            {
                return;
            }

            var data = new Dictionary<string, object>
            {
                ["siteId"] = _config.Settings.SiteID,
                ["clientSid"] = player.UserIDString,
                ["slotId"] = slotId,
            };

            SendApiRequest(
                "client.getInventorySlot",
                data,
                (code, response) =>
                {
                    if (code != 200 || string.IsNullOrEmpty(response))
                    {
                        Player.Message(player, Lang("ApiError", player.UserIDString));
                        return;
                    }

                    try
                    {
                        JObject json = JObject.Parse(response);
                        JObject slot = (JObject)json["response"];

                        if (slot == null || (int)slot["count"] <= 0)
                            return;

                        int maxCount = (int)slot["count"];
                        int quantityToTake = 1;

                        string title = (string)slot["title"] ?? "";
                        if (
                            title.IndexOf("[superstack]", StringComparison.OrdinalIgnoreCase) >= 0
                            || title.IndexOf("[суперстак]", StringComparison.OrdinalIgnoreCase) >= 0
                            || title.IndexOf("[++]", StringComparison.OrdinalIgnoreCase) >= 0
                        )
                        {
                            quantityToTake = maxCount;
                        }

                        bool isBackpack =
                            title.IndexOf("[backpack]", StringComparison.OrdinalIgnoreCase) >= 0
                            || title.IndexOf("[рюкзак]", StringComparison.OrdinalIgnoreCase) >= 0;

                        if (!isBackpack)
                        {
                            int requiredSlots = CalculateRequiredSlots(slot, quantityToTake);
                            int freeSlots =
                                player.inventory.containerMain.capacity
                                - player.inventory.containerMain.itemList.Count;
                            freeSlots +=
                                player.inventory.containerBelt.capacity
                                - player.inventory.containerBelt.itemList.Count;

                            if (freeSlots < requiredSlots)
                            {
                                // Visual feedback directly in the slot instead of chat
                                _slotStates[slotId] = $"nospace:{requiredSlots - freeSlots}";
                                DrawSingleItemSlot(player, slotId, false);

                                timer.Once(
                                    1.5f,
                                    () =>
                                    {
                                        _slotStates.Remove(slotId);
                                        DrawSingleItemSlot(player, slotId, false); // Re-draw back to normal state
                                    }
                                );

                                return;
                            }
                        }

                        var actData = new Dictionary<string, object>
                        {
                            ["siteId"] = _config.Settings.SiteID,
                            ["clientSid"] = player.UserIDString,
                            ["slotId"] = slotId,
                            ["quantity"] = quantityToTake,
                        };

                        SendApiRequest(
                            "items.activate",
                            actData,
                            (aCode, aResponse) =>
                            {
                                if (aCode == 200)
                                {
                                    GiveItemToPlayer(player, slot, quantityToTake);

                                    // Update local cache
                                    if (
                                        _playerItems.TryGetValue(
                                            player.UserIDString,
                                            out JArray pItems
                                        )
                                    )
                                    {
                                        int validItems = 0;
                                        foreach (JToken t in pItems)
                                        {
                                            JObject pItem = t as JObject;
                                            if (pItem != null)
                                            {
                                                if ((string)pItem["_id"] == slotId)
                                                {
                                                    if (maxCount > quantityToTake)
                                                    {
                                                        pItem["count"] = maxCount - quantityToTake;
                                                        validItems++;
                                                    }
                                                    else
                                                    {
                                                        pItem["count"] = 0; // Mark as fully taken
                                                    }
                                                }
                                                else if ((int)(pItem["count"] ?? 0) > 0)
                                                {
                                                    validItems++;
                                                }
                                            }
                                        }

                                        if (validItems <= 1)
                                        {
                                            CuiHelper.DestroyUi(player, UIName + "_TakeAll");
                                        }
                                    }

                                    if (maxCount > quantityToTake)
                                    {
                                        _slotStates[slotId] = "success";
                                        DrawSingleItemSlot(player, slotId, false);

                                        timer.Once(
                                            1.5f,
                                            () =>
                                            {
                                                _slotStates.Remove(slotId);
                                                DrawSingleItemSlot(player, slotId, false);
                                            }
                                        );
                                    }
                                    else
                                    {
                                        _slotStates[slotId] = "success";
                                        DrawSingleItemSlot(player, slotId, false);

                                        timer.Once(
                                            0.5f,
                                            () =>
                                            {
                                                _slotStates.Remove(slotId);
                                                DrawSingleItemSlot(player, slotId, true);
                                            }
                                        );
                                    }
                                }
                            }
                        );
                    }
                    catch (Exception ex)
                    {
                        PrintError($"Error processing item take: {ex.Message}");
                    }
                }
            );
        }

        private int CalculateRequiredSlots(JObject slot, int quantityMultiplier)
        {
            int requiredSlots = 0;
            JArray equips = (JArray)slot["content"]?["equips"];
            if (equips != null)
            {
                foreach (JToken token in equips)
                {
                    JObject equip = token as JObject;
                    if (equip == null)
                        continue;

                    JObject info = equip["info"] as JObject;
                    if (info == null)
                        continue;

                    string type = (string)info["type"];

                    if (type == "item")
                    {
                        // Each item configuration will result in 1 slot being used,
                        // as GiveItemToPlayer creates exactly 1 Item instance per equip regardless of quantityMultiplier.
                        requiredSlots++;
                    }
                }
            }

            if (requiredSlots == 0)
                requiredSlots = 1;

            return requiredSlots;
        }

        private void GiveItemToPlayer(BasePlayer player, JObject slot, int quantityMultiplier = 1)
        {
            string title = (string)slot["title"] ?? "";
            bool isBackpack =
                title.IndexOf("[backpack]", StringComparison.OrdinalIgnoreCase) >= 0
                || title.IndexOf("[рюкзак]", StringComparison.OrdinalIgnoreCase) >= 0;

            List<Item> backpackItems = isBackpack ? new List<Item>() : null;

            JArray equips = (JArray)slot["content"]?["equips"];
            if (equips != null)
            {
                foreach (JObject equip in equips)
                {
                    JObject info = (JObject)equip["info"];
                    string type = (string)info["type"];

                    if (type == "item")
                    {
                        string shortname = (string)info["bpPath"];
                        int count = (int)equip["count"] * quantityMultiplier;

                        bool isBlueprint =
                            title.IndexOf("чертеж", StringComparison.OrdinalIgnoreCase) >= 0
                            || title.IndexOf("чертёж", StringComparison.OrdinalIgnoreCase) >= 0
                            || title.IndexOf("blueprint", StringComparison.OrdinalIgnoreCase) >= 0
                            || title.IndexOf("[ч]", StringComparison.OrdinalIgnoreCase) >= 0
                            || title.IndexOf("[b]", StringComparison.OrdinalIgnoreCase) >= 0;

                        if (isBlueprint)
                        {
                            Item item = ItemManager.CreateByName("blueprintbase", count);
                            if (item != null)
                            {
                                var def = ItemManager.FindItemDefinition(shortname);
                                if (def != null)
                                    item.blueprintTarget = def.itemid;

                                if (isBackpack)
                                {
                                    backpackItems.Add(item);
                                }
                                else if (!player.inventory.GiveItem(item))
                                {
                                    item.Drop(player.GetDropPosition(), player.GetDropVelocity());
                                }
                            }
                        }
                        else
                        {
                            Item item = ItemManager.CreateByName(shortname, count);
                            if (item != null)
                            {
                                if (isBackpack)
                                {
                                    backpackItems.Add(item);
                                }
                                else if (!player.inventory.GiveItem(item))
                                {
                                    item.Drop(player.GetDropPosition(), player.GetDropVelocity());
                                }
                            }
                        }
                    }
                    else if (type == "prefab")
                    {
                        string className = (string)info["className"];
                        if (className == "vehicle")
                        {
                            for (int i = 0; i < quantityMultiplier; i++)
                            {
                                string shortname = (string)info["bpPath"];
                                BaseEntity ent = GameManager.server.CreateEntity(
                                    shortname,
                                    player.transform.position + player.transform.forward * 3f
                                );
                                if (ent != null)
                                {
                                    ent.OwnerID = player.userID;
                                    ent.Spawn();
                                }
                            }
                        }
                    }
                }
            }

            JArray cmds = (JArray)slot["content"]?["cmds"];
            if (cmds != null)
            {
                foreach (JObject cmd in cmds)
                {
                    string raw = (string)cmd["raw"];
                    if (!string.IsNullOrEmpty(raw))
                    {
                        raw = raw.Replace("{player.sid}", player.UserIDString)
                            .Replace("{player.name}", player.displayName)
                            .Replace("{item.name}", (string)slot["title"])
                            .Replace("{item.count}", "1");
                        ConsoleSystem.Run(ConsoleSystem.Option.Server.Quiet(), raw);
                    }
                }
            }

            if (isBackpack && backpackItems != null && backpackItems.Count > 0)
            {
                DropItemsInBackpack(player, backpackItems);
            }
        }

        private void DropItemsInBackpack(BasePlayer player, List<Item> items)
        {
            if (items == null || items.Count == 0)
                return;

            string prefab = "assets/prefabs/misc/item drop/item_drop_backpack.prefab";
            Vector3 pos =
                player.transform.position + player.transform.forward * 1.5f + Vector3.up * 0.5f;

            BaseEntity ent = GameManager.server.CreateEntity(prefab, pos);
            if (ent != null)
            {
                DroppedItemContainer container = ent as DroppedItemContainer;
                if (container != null)
                {
                    container.inventory = new ItemContainer();
                    container.inventory.ServerInitialize(null, items.Count);
                    container.inventory.GiveUID();

                    foreach (Item item in items)
                    {
                        if (!item.MoveToContainer(container.inventory))
                            item.Drop(pos, Vector3.zero);
                    }

                    container.Spawn();
                }
                else
                {
                    ent.Spawn();
                    foreach (var item in items)
                        item.Drop(pos, Vector3.zero);
                }
            }
            else
            {
                foreach (var item in items)
                    item.Drop(player.GetDropPosition(), player.GetDropVelocity());
            }
        }

        #endregion

        #region Localization

        protected override void LoadDefaultMessages()
        {
            lang.RegisterMessages(
                new Dictionary<string, string>
                {
                    ["Title"] = "RSurvivalStore",
                    ["Empty"] = "Your inventory is empty.",
                    ["Taken"] = "TAKEN",
                    ["ApiError"] = "Failed to communicate with the shop API.",
                    ["ItemBlocked"] = "Blocked! Time left: {0}",
                    ["NeedSlots"] = "NEED\n{0} SLOTS",
                    ["BlockedInTC"] = "You cannot open the store in an enemy building zone.",
                    ["BlockedInRaid"] = "You cannot open the store while raid blocked.",
                    ["BlockedInCombat"] = "You cannot open the store while in combat.",
                    ["NotConfigured"] = "The store is not yet configured by the administrator.",
                    ["NotAuthorized"] = "You are not authorized in the store!\nPlease log in at:",
                },
                this
            );

            lang.RegisterMessages(
                new Dictionary<string, string>
                {
                    ["Title"] = "RSurvivalStore",
                    ["Empty"] = "Ваша корзина пуста.",
                    ["Taken"] = "ВЗЯТО",
                    ["ApiError"] = "Ошибка связи с сервером магазина.",
                    ["ItemBlocked"] = "Блок! Осталось: {0}",
                    ["NeedSlots"] = "НУЖНО\n{0} СЛОТОВ",
                    ["BlockedInTC"] = "Вы не можете открыть магазин в зоне чужого шкафа.",
                    ["BlockedInRaid"] = "Вы не можете открыть корзину во время рейд блока.",
                    ["BlockedInCombat"] = "Вы не можете открыть корзину во время комбат блока.",
                    ["NotConfigured"] = "Магазин еще не настроен администратором.",
                    ["NotAuthorized"] =
                        "Вы не авторизованы в магазине!\nПожалуйста, авторизуйтесь по адресу:",
                },
                this,
                "ru"
            );
        }

        private string Lang(string key, string id = null, params object[] args)
        {
            return args.Length == 0
                ? lang.GetMessage(key, this, id)
                : string.Format(lang.GetMessage(key, this, id), args);
        }

        #endregion
    }
}
