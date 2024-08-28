// Reference: Facepunch.Sqlite
// Reference: UnityEngine.UnityWebRequestModule
// Copyright SurvivalShop.org
using System;
using System.Linq;
using System.Text;
using System.Collections.Generic;
using System.Security.Cryptography;
using Oxide.Core;
using Oxide.Game.Rust.Libraries;
using Oxide.Game.Rust.Cui;
using Rust;
using UnityEngine;
using UnityEngine.Networking;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using static Explosion_Bloom;
using System.ComponentModel;
using UnityEngine.UI;
using System.Drawing.Drawing2D;
namespace Oxide.Plugins {
  [Description("SurvivalShop is a plugin for server monetization")]
  [Info("SurvivalShop", "SurvivalShop.org, clickable GUI by Sth", "2.7.0")]
  public class SurvivalShop : RustPlugin {
    SurvivalShop Plugin;
    private const string PermissionSkipQueue = "survivalshop.skipqueue";
    Dictionary<string, PlayerData> Players =
        new Dictionary<string, PlayerData>();
    public class PlayerData {
      public BasePlayer Player;
      public string PlayerId;
      public JArray Inventory;
      public int InventoryCount;
      public DateTime InventoryTime;
      public int InventoryPage;
      public int InventoryTotalPages;
      public bool InventoryNoTimer;
      public bool InventoryShown;
      public bool InventoryShowTip;
      public int InventoryShowTipCounter;
      public Lang Language;
      SurvivalShop SurvivalShop;
      public Lang Locale {
        get { return SurvivalShop.Locale(Player); }
      }
      public PlayerData(SurvivalShop shop, BasePlayer player) {
        SurvivalShop = shop;
        Player = player;
        PlayerId = player.UserIDString;
        if (PlayerId == null) PlayerId = "*anonymous";
      }
    }
    new PlayerData Player(BasePlayer player) {
      PlayerData data;
      if (player == null) return null;
      if (!Players.TryGetValue(player.UserIDString, out data)) {
        data = new PlayerData(this, player);
        Players[player.UserIDString] = data;
      }
      return data;
    }
    public delegate void LockCallback();
    static void ThreadLock(string state, LockCallback callback,
                           ref bool lock_param, object lock_ref) {
      ThreadLockState = state;
      if (lock_param) ThreadLockCounter++;
      lock(lock_ref) {
        try {
          lock_param = true;
          callback();
        } finally {
          lock_param = false;
          ThreadLockState = null;
        }
      }
    }
    static string ThreadLockState;
    static long ThreadLockCounter = 0;
    public string SiteId;
    public string ApiKey;
    public bool DebugEnabled;
    public bool AutofuelEnabled;
    public bool NoWelcomeTitle;
    public bool UseTranslit;
    public bool Auto;
    public bool ServerInitialized;
    public Dictionary<string, List<ApiRequest>> ApiRequests;
    public bool ApiRequestsLock;
    public Dictionary<string, JObject> PowerupActivations;
    public bool PowerupActivationsLock;
    public List<ApiResponse> ApiResponses;
    public bool ApiResponsesLock;
    public bool ShopRegistered;
    public string ShopServerId;
    public string ShopLink;
    public bool ShopServerPremium;
    public string ShopHello;
    DateTime NextRegister;
    DateTime NextTimeoutCheck;
    public class ApiRequest {
      public SurvivalShop Plugin;
      public string MethodName;
      public PlayerData Player;
      public UnityWebRequest Request;
      public DateTime StartTime;
      public DateTime TimeoutTime;
      public Coroutine Routine;
      public static string ImageUrl = "http://survivalshop.org";
      public static string ApiUrl = "http://api.survivalshop.org";
      public Dictionary<string, object> Data;
      public ApiFunc Completed;
      public string RequestFor;
      public bool Canceled;
      public class BypassCertificate : CertificateHandler {
        protected override bool ValidateCertificate(byte[] certificateData) {
          return true;
        }
      }
      System.Collections.IEnumerator ExecWait(ApiError failed = null) {
        yield return Request.SendWebRequest();
        try {
          ThreadLock("Api requests", () => {
            List<ApiRequest> requests;
            if (Plugin.ApiRequests.TryGetValue(RequestFor, out requests))
              requests.Remove(this);
          }, ref Plugin.ApiRequestsLock, Plugin.ApiRequests);
          JObject response_data = null;
          JToken error_code = null;
          var error = Request.error;
          var text = Request.downloadHandler.text;
          if (error == null && (text.Length == 0 || text.Length > 1024 * 1024))
            error = "Response is null or too large";
          if (error == null) {
            var data = Request.downloadHandler.text + "";
            try {
              response_data = JObject.Parse(Request.downloadHandler.text);
            } catch (Exception e) {
              error = "Failed to parse response: " + e.Message +
                      ". Data was (first 50 bytes): '" +
                      (data.Length > 50 ? data.Substring(0, 50) : data) + "'";
            }
          }
          if (error == null && response_data == null)
            error = "Response data is null";
          if (error == null &&
              response_data.TryGetValue("error_code", out error_code))
            error = response_data ["error_msg"]
                        .ToString();
          if (!string.IsNullOrEmpty(error)) {
            Plugin.Error("Api request {0} failed for {1}: {2}", MethodName,
                         RequestFor, error);
            try {
              failed?.Invoke(this, error);
            } catch (Exception e) {
              Plugin.Error(e, "ApiError");
            }
          } else {
            Plugin.Debug("Finished request {0} for {1}: {2}", MethodName,
                         RequestFor, Request.downloadHandler.text,
                         Request.error);
            if (!Canceled)
              ThreadLock("Api responses", () => {
                Plugin.ApiResponses.Add(
                    new ApiResponse(this, response_data, Completed, text));
              }, ref Plugin.ApiRequestsLock, Plugin.ApiResponses);
          }
          Request = null;
        } catch (Exception e) {
          Plugin.Error("Failed to run request {0} for {1}: {2}", MethodName,
                       RequestFor, e.Message);
        }
      }
      public void Execute(ApiError failed = null) {
        Plugin.Debug("Started request {0} for {1}", MethodName, RequestFor);
        ThreadLock("Api requests", () => {
          List<ApiRequest> requests;
          if (Plugin.ApiRequests.TryGetValue(RequestFor, out requests))
            requests.Add(this);
          else {
            requests = new List<ApiRequest>();
            requests.Add(this);
            Plugin.ApiRequests.Add(RequestFor, requests);
          }
        }, ref Plugin.ApiRequestsLock, Plugin.ApiRequests);
        var salt = "salt";
        var data_json = JsonConvert.SerializeObject(Data, Formatting.None);
        var bytes = new SHA256CryptoServiceProvider().ComputeHash(
            Encoding.UTF8.GetBytes(data_json + Plugin.ApiKey + salt));
        var hex = new StringBuilder(bytes.Length * 2);
        for (int i = 0; i < bytes.Length; i++)
          hex.AppendFormat("{0:x2}", bytes[i]);
        Data["__sign"] = hex.ToString() + ":" + Plugin.SiteId + ":" + salt;
        var serialized_data =
            JsonConvert.SerializeObject(Data, Formatting.None);
        Plugin.Debug("Api request data {0}", serialized_data);
        Request = new UnityWebRequest(ApiUrl + "/" + MethodName, "POST");
        Request.certificateHandler = new BypassCertificate();
        Request.uploadHandler =
            new UploadHandlerRaw(Encoding.UTF8.GetBytes(serialized_data));
        Request.downloadHandler = new DownloadHandlerBuffer();
        Request.SetRequestHeader("Content-Type", "application/json");
        Routine = Rust.Global.Runner.StartCoroutine(ExecWait(failed));
      }
      public void Cancel() {
        Canceled = true;
        Plugin.Debug("Cancelling request {0} for {1}", MethodName, RequestFor);
        if (Routine != null) Rust.Global.Runner.StopCoroutine(Routine);
        Routine = null;
      }
      public ApiRequest(SurvivalShop shop, PlayerData player,
                        string method_name, Dictionary<string, object> data,
                        ApiFunc completed) {
        Plugin = shop;
        Player = player;
        RequestFor = Player == null? "*" : Player.PlayerId;
        StartTime = DateTime.Now;
        TimeoutTime = StartTime.AddSeconds(30);
        MethodName = method_name;
        Data = data;
        Completed = completed;
      }
    }
    public class ApiResponse {
      public ApiRequest Request;
      public JObject Data;
      public ApiFunc Completed;
      public string ResponseString;
      public ApiResponse(ApiRequest request, JObject data, ApiFunc completed,
                         string response_string) {
        Request = request;
        Data = data;
        Completed = completed;
        ResponseString = response_string;
      }
    }
    public delegate void ApiFunc(ApiResponse result);
    public delegate void ApiError(ApiRequest request, string error);
    void ApiExec(PlayerData player, string method_name,
                 Dictionary<string, object> data, ApiFunc completed,
                 ApiError failed = null) {
      new ApiRequest(this, player, method_name, data, completed)
          .Execute(failed);
    }
    void ApiFailed(PlayerData player, ApiRequest request, string error) {
      if (player == null) return;
      if (!string.IsNullOrEmpty(error) &&
          (error.Contains(
               "Mesh not exist",
               System.Globalization.CompareOptions.OrdinalIgnoreCase) ||
           error.Contains(
               "Клиент не найден",
               System.Globalization.CompareOptions.OrdinalIgnoreCase)))
        Chat(player, player.Locale.MeshNotExists);
      else
        Chat(player, player.Locale.ApiRequestError);
    }
    void ApiInit() {
      ApiRequests = new Dictionary<string, List<ApiRequest>>();
      ApiRequestsLock = false;
      PowerupActivations = new Dictionary<string, JObject>();
      PowerupActivationsLock = false;
      ApiResponses = new List<ApiResponse>();
      ApiResponsesLock = false;
    }
    void ApiFrame() {
      if (DateTime.Now > NextRegister && !string.IsNullOrEmpty(SiteId) &&
          !string.IsNullOrEmpty(ApiKey)) {
        var was_registered = ShopRegistered;
        ApiExec(
            null, "servers.register2",
            new Dictionary<string, object>(){
                {"siteId", SiteId},
                {"serverData",
                 new {game = "RUST", name = ConVar.Server.hostname,
                      port = ConVar.Server.queryport,
                      online = BasePlayer.activePlayerList.Count,
                      maxplayers = ConVar.Server.maxplayers,
                      pluginName = "SurvivalShop", pluginVersion = "2.7.0"}}},
            (r) => {
              var response = r.Data["response"];
              ShopRegistered = true;
              ShopServerId = response ["id"]
                                 .ToString();
              ShopServerPremium = (bool) response["premium"];
              ShopLink = (string) response["shopUrl"];
              ShopHello = response ["hello"]
                              .ToString();
              if (!was_registered)
                Plugin.Notify(Locale().ServerRegister, SiteId, ShopServerId);
              if (!was_registered) InvStartAll();
              if (response["powerups"] != null)
                Plugin.PowerupsAcquire((JArray) response["powerups"]);
            },
            (r, err) => {
              ShopRegistered = false;
              InvStopAll();
            });
        NextRegister = DateTime.Now.AddMinutes(1);
      }
      if (DateTime.Now > NextTimeoutCheck) {
        var now = DateTime.Now;
        ThreadLock("Api requests", () => {
          foreach (var kv in ApiRequests)
            for (int i = 0; i < kv.Value.Count; i++) {
              if (now > kv.Value[i].TimeoutTime) {
                kv.Value [i]
                    .Cancel();
                kv.Value.Remove(kv.Value[i]);
              }
            }
        }, ref Plugin.ApiRequestsLock, Plugin.ApiRequests);
        NextTimeoutCheck = DateTime.Now.AddMinutes(2);
      }
    }
    void ApiTick() {
      ThreadLock("Api responses", () => {
        if (ApiResponses.Count > 0) {
          for (int i = 0; i < ApiResponses.Count; i++) {
            try {
              ApiResponses [i]
                  .Completed(ApiResponses[i]);
            } catch (Exception e) {
              Error(e, "ApiResponse (" + ApiResponses[i].Request.MethodName +
                           ")" +
                           (ApiResponses[i].Request.Player !=
                                null? " for " +
                                    ApiResponses[i].Request.Player.PlayerId
                            : ""));
            }
          }
          ApiResponses.Clear();
        }
      }, ref Plugin.ApiResponsesLock, Plugin.ApiResponses);
    }
    void ApiUnload() { ApiRequests.Clear(); }
    string SafeStringFormat(string format, object[] args = null) {
      if (args == null) return format;
      for (int i = 0; i < args.Length; i++)
        format = format.Replace("{" + i + "}", args[i] == null? "null"
                                : args [i]
                                      .ToString());
      return format;
    }
    void Notify(string format, params object[] args) {
      var msg = SafeStringFormat(format, args);
      LogToFile("log", msg, Plugin);
      if (UseTranslit) msg = Locale().Translit(msg);
      Puts(msg);
    }
    void Debug(string format, params object[] args) {
      if (!DebugEnabled) return;
      var msg = SafeStringFormat(format, args);
      LogToFile("log", "DEBUG " + msg, Plugin);
      if (UseTranslit) msg = Locale().Translit(msg);
      Puts("Debug " + msg);
    }
    void Error(string format, params object[] args) {
      var msg = SafeStringFormat(format, args);
      LogToFile("log", "ERROR " + msg, Plugin);
      LogToFile("error", "ERROR " + msg, Plugin);
      PrintError(msg);
    }
    void Error(Exception e, string function_name, string details = null) {
      var msg =
          "In " + function_name + ": " + e.Message +
          ", callstack: " + e.StackTrace +
          (string.IsNullOrEmpty(details) ? "." : ", details: " + details + ".");
      if (e.InnerException != null)
        msg = msg + "Inner in " + function_name + ": " +
              e.InnerException.Message +
              ", callstack: " + e.InnerException.StackTrace + ".";
      LogToFile("log", "ERROR " + msg, Plugin);
      LogToFile("error", "ERROR " + msg, Plugin);
      PrintError(msg);
    }
    void Warning(string format, params object[] args) {
      var msg = SafeStringFormat(format, args);
      LogToFile("log", "WARNING " + msg, Plugin);
      PrintWarning(msg);
    }
    void Chat(BasePlayer player, string message, params object[] args) {
      player.ChatMessage("Shop: " + SafeStringFormat(message, args));
    }
    void Chat(PlayerData player, string message, params object[] args) {
      player.Player.ChatMessage(player.Locale.Shop + ": " +
                                SafeStringFormat(message, args));
    }
    Lang LocaleEn;
    Lang LocaleRu;
    Lang LocaleCurrent;
    public class Lang {
      public virtual string Locale => "EN";
      public virtual string Shop => "Shop";
      public virtual string ConfigFailedToLoad => "Failed to load config: {0}";
      public virtual string
          InventoryFailedToShow => "Failed to show inventory: {0} ({1})";
      public virtual string
          InventoryFailedToTake => "Failed to take inventory: {0} ({1})";
      public virtual string
          InventoryFailedToTakeAll => "Failed to take all inventory: {0} ({1})";
      public virtual string Loading => "Loading inventory data...";
      public virtual string
          MySurvivalShopInventory => "My SurvivalShop Inventory";
      public virtual string Paging => "page {0} of {1}";
      public virtual string Tip => "Tip:";
      public virtual string QuickBindHelp=>"How to set up quick access: hit F1 (console), then type:";
      public virtual string QuickBindHelp2 => "bind f2 chat.say /shopinvt";
      public virtual string
          ShopNotRegistered => "Sorry, shop is not registered";
      public virtual string ShopUsage => "Usage {0} <Page number>";
      public virtual string ShopGiveUsage => "Usage {0} <Index> [Quantity]";
      public virtual string
          NoSuchPosition => "Sorry, no such inventory position";
      public virtual string PlayerNotFound => "Sorry, player not found";
      public virtual string SlotNotFound => "Sorry, slot not found";
      public virtual string MeshNotExists=>"Sorry, store account not found. Please log in to this server's store site and try again.";
      public virtual string ApiRequestError=>"Sorry, request error occured, please try again later";
      public virtual string
          RuntimeError => "Sorry, runtime error occured, please try again later";
      public virtual string
          FailedToUseInventory => "Sorry, failed to use inventory";
      public virtual string FailedToUseSlot => "sorry, failed to use slot";
      public virtual string Activated_0_1 => "Activated: {0} {1}";
      public virtual string Delivered_0_1 => "Delivered: {0} {1}";
      public virtual string Unlocked_0_1 => "Blueprint unlocked: {0} {1}";
      public virtual string
          CountNotEnough => "Sorry, not enough items in inventory";
      public virtual string Purshase_0_for_1_ => "purshase '{0}' for {1} - ";
      public virtual string
          YourCharacterMustBeConcious => "Your character must be conscious";
      public virtual string
          CannotActivate_0_1 => "Sorry, failed to activate {0} - {1}";
      public virtual string
          CannotDeliver_0_1 => "Sorry, failed to deliver {0} - {1}";
      public virtual string
          CannotUnlock_0_1 => "Sorry, failed to unlock {0} - {1}";
      public virtual string ActivationError => "activation error";
      public virtual string DeliveryError => "delivery error";
      public virtual string UnlockError => "unlock error";
      public virtual string
          RequireMoreSlots => "need to free up {0} inventory slots";
      public virtual string ItemNotFound => "item {0} not found";
      public virtual string LocaleSetUp => "Switched language to EN";
      public virtual string
          LocaleFailed => "Sorry, failed to switch language: {0}";
      public virtual string SetupOk=>"Shop keys written succesfully! Type survivalshop.status for detailed state information";
      public virtual string Status01=>"Registered: {0} (siteId {1}, initialized {2}, debug {3}, autofuel {4}, api {5}, next_register {6})";
      public virtual string Status02=>"Server ID: {0} (hello {1}), lock state '{2}' (counter {3}) auto {4}";
      public virtual string Status03 => "Locale: {0}";
      public virtual string Status04 => "Players: {0}";
      public virtual string Status05 => "Active requests: {0}";
      public virtual string
          ShopSetupUsage => "Usage {0} <SiteId> <SiteKey> [Locale]";
      public virtual string
          ServerRegister => "Shop registered with site {0}, server Id {1}";
      public virtual string AcquiredPowerups => "Acquired powerups: {0}";
      public virtual string
          PowerupDelivered_for_0_1 => "Powerup delivered for {0}: {1}";
      public virtual string TitleHelp=>"Take item: /give <position> [count] or /giveall [position]";
      public virtual string TitleHelp2 => "Switch page: /shop <page>";
      public virtual string TitleHelp3 => "Switch language: /en, /ru";
      public virtual string ShopHello=>"Welcome to SurvivalShop!\nSign in to check your bonuses, or gain more!\nThen use /shop to view your inventory\nShop site: {0}";
      public virtual string Close => "Close";
      public virtual string BuildingBlockedHere=>"Sorry, failed to deliver: construction here is blocked";
      public virtual string OptionIsNowOn => "{0} is now ON";
      public virtual string OptionIsNowOff => "{0} is now OFF";
      public virtual string OptionUsage => "Usage {0} 0/1";
      public virtual string Translit(string s) { return s; }
    }
    public class LangRu : Lang {
      public override string Locale => "RU";
      public override string Shop => "Магазин";
      public override string ConfigFailedToLoad =>
          "Ошибка загрузки конфигурации: {0}";
      public override string InventoryFailedToShow =>
          "Ошибка показа инвентаря: {0} ({1})";
      public override string InventoryFailedToTake =>
          "Ошибка выдачи инвентаря: {0} ({1})";
      public override string InventoryFailedToTakeAll =>
          "Ошибка выдачи всего инвентаря: {0} ({1})";
      public override string Loading => "Загружаем данные инвентаря...";
      public override string MySurvivalShopInventory =>
          "Мой инвентарь SurvivalShop";
      public override string Paging => "стр. {0} из {1}";
      public override string Tip => "Совет:";
      public override string QuickBindHelp =>
          "Доступ с клавиши: нажать F1 (консоль), затем набрать:";
      public override string ShopNotRegistered =>
          "Извините, магазин не зарегистрирован";
      public override string ShopUsage => "Формат {0} <Номер страницы>";
      public override string ShopGiveUsage =>
          "Формат {0} <Индекс> [Количество] или !в <Индекс> [Количество]";
      public override string NoSuchPosition =>
          "Извините, нет такой позиции в инвентаре";
      public override string PlayerNotFound => "Извините, игрок не найден";
      public override string SlotNotFound => "Извините, cлот не найден";
      public override string MeshNotExists =>
          "Извините, счет в магазине не найден. Пожалуйста выполните вход на сайте магазина этого сервера и попробуйте снова.";
      public override string ApiRequestError =>
          "Извините, произошла ошибка запроса, попробуйте еще раз позднее";
      public override string RuntimeError =>
          "Извините, произошла ошибка выполнения, попробуйте еще раз позднее";
      public override string FailedToUseInventory =>
          "Извините, не удалось использовать инвентарь";
      public override string FailedToUseSlot =>
          "Извините, не удалось использовать слот";
      public override string Activated_0_1 => "Активировано: {0} {1}";
      public override string Delivered_0_1 => "Выдано: {0} {1}";
      public override string Unlocked_0_1 => "Чертеж разблокирован: {0} {1}";
      public override string CountNotEnough =>
          "Извините, недостаточно предметов в инвентаре";
      public override string Purshase_0_for_1_ => "покупка '{0}' для {1} - ";
      public override string YourCharacterMustBeConcious =>
          "Ваш персонаж должен быть в сознании";
      public override string CannotActivate_0_1 =>
          "Извините, не удалось активировать {0} - {1}";
      public override string CannotDeliver_0_1 =>
          "Извините, не удалось выдать {0} - {1}";
      public override string CannotUnlock_0_1 =>
          "Извините, не удалось разблокировать {0} - {1}";
      public override string ActivationError => "ошибка при активации";
      public override string DeliveryError => "ошибка при выдаче";
      public override string UnlockError => "ошибка при разблокировке";
      public override string RequireMoreSlots =>
          "необходимо освободить {0} слотов в инвентаре";
      public override string ItemNotFound => "предмет {0} не найден";
      public override string LocaleSetUp => "Установлен язык RU";
      public override string LocaleFailed =>
          "Извините, не удалось установить язык: {0}";
      public override string SetupOk =>
          "Ключи прописаны успешно! Введите survivalshop.status для получения подробной информации о состоянии";
      public override string Status01 =>
          "Регистрация: {0} (siteId {1}, initialized: {2}, debug {3}, autofuel {4}, api {5}, next_register {6})";
      public override string Status02 =>
          "ID сервера: {0} (приветствие {1}) фиксация потока '{2}' (счетчик {3}) авто {4}";
      public override string Status03 => "Локализация: {0}";
      public override string Status04 => "Игроки: {0}";
      public override string Status05 => "Активные запросы: {0}";
      public override string ShopSetupUsage =>
          "Формат {0} <SiteId> <SiteKey> [Locale]";
      public override string ServerRegister =>
          "Магазин зарегистрирован с ID сайта {0}, ID сервера {1}";
      public override string AcquiredPowerups =>
          "Получены бонусные товары: {0}";
      public override string PowerupDelivered_for_0_1 =>
          "Выдан бонус для {0}: {1}";
      public override string TitleHelp =>
          "Получить предмет: /give <позиция> [кол-во] или /giveall [позиция] или /в <позиция> [кол-во] или /вв [позиция]";
      public override string TitleHelp2 =>
          "Переключить страницу: /shop <page> или /м <страница>";
      public override string TitleHelp3 => "Переключить язык: /en, /ru";
      public override string ShopHello =>
          "Привет от SurvivalShop!\nАвторизуйтесь на сайте, чтобы получить бонусы!\nНаберите /shop, чтобы просмотреть инвентарь\nСайт магазина: {0}";
      public override string Close => "Закрыть";
      public override string BuildingBlockedHere =>
          "Извините, не удалось доставить: строительство здесь заблокировано";
      public override string OptionIsNowOn => "{0} теперь ВКЛ";
      public override string OptionIsNowOff => "{0} теперь ВЫКЛ";
      public override string OptionUsage => "Формат {0} 0/1";
      static Dictionary<char, string> TranslitChars =
          new Dictionary<char, string>(){
              {'Є', "EH"},  {'І', "I"},  {'і', "i"},  {'є', "eh"},  {'А', "A"},
              {'Б', "B"},   {'В', "V"},  {'Г', "G"},  {'Д', "D"},   {'Е', "E"},
              {'Ё', "JO"},  {'Ж', "ZH"}, {'З', "Z"},  {'И', "I"},   {'Й', "JJ"},
              {'К', "K"},   {'Л', "L"},  {'М', "M"},  {'Н', "N"},   {'О', "O"},
              {'П', "P"},   {'Р', "R"},  {'С', "S"},  {'Т', "T"},   {'У', "U"},
              {'Ф', "F"},   {'Х', "KH"}, {'Ц', "C"},  {'Ч', "CH"},  {'Ш', "SH"},
              {'Щ', "SHH"}, {'Ъ', "'"},  {'Ы', "Y"},  {'Ь', ""},    {'Э', "EH"},
              {'Ю', "YU"},  {'Я', "YA"}, {'а', "a"},  {'б', "b"},   {'в', "v"},
              {'г', "g"},   {'д', "d"},  {'е', "e"},  {'ё', "jo"},  {'ж', "zh"},
              {'з', "z"},   {'и', "i"},  {'й', "jj"}, {'к', "k"},   {'л', "l"},
              {'м', "m"},   {'н', "n"},  {'о', "o"},  {'п', "p"},   {'р', "r"},
              {'с', "s"},   {'т', "t"},  {'у', "u"},  {'ф', "f"},   {'х', "kh"},
              {'ц', "c"},   {'ч', "ch"}, {'ш', "sh"}, {'щ', "shh"}, {'ъ', ""},
              {'ы', "y"},   {'ь', ""},   {'э', "eh"}, {'ю', "yu"},  {'я', "ya"},
              {'«', ""},    {'»', ""},   {'—', "_"}};
      public override string Translit(string s) {
        var sb = new StringBuilder();
        if (!string.IsNullOrEmpty(s))
          for (int i = 0; i < s.Length; i++)
            if (TranslitChars.TryGetValue(s[i], out var t))
              sb.Append(t);
            else
              sb.Append(s[i]);
        return sb.ToString();
      }
    }
    void LocaleInit() {
      LocaleEn = new Lang();
      LocaleRu = new LangRu();
      LocaleCurrent = LocaleEn;
    }
    Lang Locale() { return LocaleCurrent; }
    Lang Locale(PlayerData player) {
      if (player.Language == null) return Locale();
      return player.Language;
    }
    Lang Locale(BasePlayer player) { return Locale(Player(player)); }
    void LocaleSet(PlayerData player, Lang locale) {
      player.Language = locale;
      Chat(player, Locale(player).LocaleSetUp);
    }
    void LocaleUnload() {}
    class Configuration {
      [JsonProperty("siteId")]
      public string SiteId;
      [JsonProperty("siteKey")]
      public string SiteKey;
      [JsonProperty("locale")]
      public string Locale;
      [JsonProperty("debug")]
      public bool Debug = false;
      [JsonProperty("auto")]
      public bool Auto = false;
      [JsonProperty("autofuel")]
      public bool Autofuel = true;
      [JsonProperty("noWelcomeTitle")]
      public bool NoWelcomeTitle = false;
      [JsonProperty("useTran")]
      public bool UseTranslit = false;
      internal void Set(string name, object value) {
        if (name == "siteId") SiteId = (string) value;
        if (name == "siteKey") SiteKey = (string) value;
        if (name == "locale") Locale = (string) value;
        if (name == "debug") Debug = (bool) value;
        if (name == "auto") Auto = (bool) value;
        if (name == "autofuel") Autofuel = (bool) value;
        if (name == "noWelcomeTitle") NoWelcomeTitle = (bool) value;
        if (name == "useTran") UseTranslit = (bool) value;
      }
    }
    protected override void LoadConfig() {
      base.LoadConfig();
      var config = new Configuration();
      try {
        config = Config.ReadObject<Configuration>();
      } catch (Exception e) {
        Error(e, "LoadConfig");
      }
      SiteId = config.SiteId;
      ApiKey = config.SiteKey;
      DebugEnabled = config.Debug;
      AutofuelEnabled = config.Autofuel;
      NoWelcomeTitle = config.NoWelcomeTitle;
      LocaleCurrent = LocaleEn;
      UseTranslit = config.UseTranslit;
      Auto = config.Auto;
      var locale = config.Locale;
      if (locale != null) switch (locale.ToLower()) {
          case "en":
            LocaleCurrent = LocaleEn;
            break;
          case "ru":
            LocaleCurrent = LocaleRu;
            break;
        }
      Debug("SITE_ID: {0}", SiteId);
      Debug("Auto: {0}", Auto);
      NextRegister = DateTime.MinValue;
    }
    protected void SetupConfig(string site_id, string site_key, string locale) {
      var config = new Configuration();
      try {
        config = Config.ReadObject<Configuration>();
      } catch (Exception e) {
        Error(e, "LoadConfig");
      }
      config.SiteId = site_id;
      config.SiteKey = site_key;
      if (locale != null) config.Locale = locale;
      Config.WriteObject<Configuration>(config);
      LoadConfig();
    }
    protected bool SetupBool(string option, string value) {
      var config = new Configuration();
      try {
        config = Config.ReadObject<Configuration>();
      } catch (Exception e) {
        Error(e, "LoadConfig");
      }
      int v;
      bool ov;
      if (int.TryParse(value, out v))
        ov = v == 0 ? false : true;
      else if (value == "1" || value == "on" || value == "true" ||
               value == "yes")
        ov = true;
      else
        ov = true;
      config.Set(option, ov);
      Config.WriteObject<Configuration>(config);
      LoadConfig();
      return ov;
    }
    string InvName = "SurvivalshopInventoryCUI";
    int InventoryItemsPerPage = 10;
    DateTime NextTimerUpdate;
    public class ItemAffixes {
      public bool IsBlueprint;
      static string[] BlueprintAffixes =
          new string[]{"чертеж", "чертёж", "blueprint", "[ч]", "[b]"};
      internal ItemAffixes(string title) {
        for (int i = 0; i < BlueprintAffixes.Length; i++)
          if (title.Contains(
                  BlueprintAffixes[i],
                  System.Globalization.CompareOptions.OrdinalIgnoreCase)) {
            IsBlueprint = true;
            break;
          }
      }
    }
    void InvInit() {}
    void InvStart(BasePlayer player, bool welcome = true) {
      if (Auto) return;
      if (!NoWelcomeTitle && welcome)
        Chat(player, Player(player).Locale.ShopHello, ShopLink);
      InvLinkDraw(player);
    }
    void InvStartAll() {
      for (var i = 0; i < BasePlayer.activePlayerList.Count; i++)
        InvStart(BasePlayer.activePlayerList[i]);
    }
    void InvStopAll() {
      for (var i = 0; i < BasePlayer.activePlayerList.Count; i++) {
        InvUndraw(BasePlayer.activePlayerList[i]);
        InvLinkUndraw(BasePlayer.activePlayerList[i]);
      }
    }
    int InventoryStyleFontSize = 20;
    string InventoryStyleBackgroundColor = "1 1 1 0";
    string InventoryStyleAnchorMin = "0.22 0.31";
    string InventoryStyleAnchorMax = "0.78 0.89";
    string InventoryStyleOverlayBackgroundColor = "1 1 1 0";
    string InventoryStyleOverlayAnchorMin = "0.013 0.02";
    string InventoryStyleOverlayAnchorMax = "0.987 0.98";
    string InventoryStyleTitleColor = "0.9 0.9 0.9 1";
    string InventoryStyleTitleOutline = "1 0.5";
    string InventoryStyleBorderColor = "0.26 0.26 0.20 1";
    string InventoryStyleButtonBackgroundColor = "0.5 0.5 0.5 0.5";
    string InventoryStyleButtonTextColor = "0.91 0.87 0.83 1";
    string InventoryStyleButtonPressedColor = "0.2 0.2 0.2 0.5";
    string InventoryStyleTipColor = "0.9 0.9 0.9 0.7";
    void InvDraw(PlayerData player, bool redraw = false) {
      try {
        if (player == null) throw new Exception("player is null");
        InvUndraw(player.Player);
        var ui = new CuiElementContainer();
        var font = "robotocondensed-bold.ttf";
        var font_tip = "robotocondensed-regular.ttf";
        var font_size = InventoryStyleFontSize;
        var fade_in = redraw? 0.0f : 0.2f;
        ui.Add(
            new CuiPanel{Image = {Color = InventoryStyleBackgroundColor,
                                  FadeIn = fade_in},
                         RectTransform = {AnchorMin = InventoryStyleAnchorMin,
                                          AnchorMax = InventoryStyleAnchorMax}},
            "Overlay", $"{InvName}");
        ui.Add(
            new CuiPanel{
                Image = {Color = InventoryStyleOverlayBackgroundColor,
                         FadeIn = fade_in},
                RectTransform = {AnchorMin = InventoryStyleOverlayAnchorMin,
                                 AnchorMax = InventoryStyleOverlayAnchorMax},
            },
            $"{InvName}", $"{InvName}Overlay");
        ui.Add(new CuiElement{
            Name = $"{InvName}Title", Parent = $"{InvName}Overlay",
            Components = {
                new CuiTextComponent{
                    Text = Locale(player).MySurvivalShopInventory,
                    Color = InventoryStyleTitleColor, Font = font,
                    FontSize = font_size, Align = TextAnchor.MiddleLeft,
                    FadeIn = fade_in},
                new CuiOutlineComponent{Distance = InventoryStyleTitleOutline,
                                        Color = "0 0 0 0.8"},
                new CuiRectTransformComponent{
                    AnchorMin = $"0 {11/12f}",
                    AnchorMax = "1 1",
                }}});
        ui.Add(new CuiElement{
            Name = $"{InvName}ShopUrl", Parent = $"{InvName}Overlay",
            Components = {
                new CuiTextComponent{
                    Text = "https://" + ShopLink,
                    Color = InventoryStyleTitleColor,
                    Font = font,
                    FontSize = font_size,
                    Align = TextAnchor.MiddleRight,
                    FadeIn = fade_in,
                },
                new CuiOutlineComponent{Distance = InventoryStyleTitleOutline,
                                        Color = "0 0 0 0.8"},
                new CuiRectTransformComponent{
                    AnchorMin = $"0 {11/12f}",
                    AnchorMax = "1.0 1.0",
                }}});
        ui.Add(
            new CuiPanel{
                Image = {Color = InventoryStyleBorderColor,
                         Material = "assets/content/ui/uibackgroundblur.mat",
                         FadeIn = fade_in},
                RectTransform = {AnchorMin = $"0 {11/12f+0.002f}",
                                 AnchorMax = $"1 {11/12f+0.008f}"},
            },
            $"{InvName}Overlay", $"{InvName}Start");
        ui.Add(
            new CuiPanel{
                Image = {Color = "0.25 0.25 0.25 0.75",
                         Material = "assets/content/ui/uibackgroundblur.mat",
                         FadeIn = fade_in},
                RectTransform = {AnchorMin = $"0 {1/12f+0.01f}",
                                 AnchorMax = $"1 {11/12f}"},
                CursorEnabled = true},
            $"{InvName}Overlay", $"{InvName}Items");
        var base_line = 1.0f;
        var num_button_paddings =
            Math.Ceiling(9.0f * (23.0f / (float) InventoryStyleFontSize));
        if (player.Inventory == null) {
          ui.Add(
              new CuiLabel{
                  RectTransform = {AnchorMin = $"0 {1/12f+0.01f}",
                                   AnchorMax = $"1 {11/12f}"},
                  Text = {Color = InventoryStyleButtonTextColor,
                          Text = Locale(player).Loading, Font = font,
                          FontSize = font_size - 4,
                          Align = TextAnchor.MiddleCenter},
              },
              $"{InvName}Overlay", $"{InvName}ItemsLoading");
        } else
          for (int i = 0; i < player.Inventory.Count;
               i++, base_line = base_line - 0.1f) {
            var item = player.Inventory[i] as JObject;
            if (item == null) {
              Error("Inventory item #{0} is null for player {0}", i,
                    player.PlayerId);
              continue;
            }
            var count = (int) item["count"];
            var title = item ["title"]
                            .ToString();
            if (title == null) {
              Error("Inventory item #{0} title is null for player {0}", i,
                    player.PlayerId);
              continue;
            }
            if (title.Length > 50) title = title.Substring(0, 50) + "...";
            var content = item["content"] as JObject;
            var cached = (string) null;
            var logo_link = GetImage(item ["logoLink"]
                                         .ToString(),
                                     out cached);
            if (content != null) {
              var equips = content["equips"] as JArray;
              if (equips != null && equips.Count == 1) {
                var spawned = (int) equips [0]
                ["count"];
                if (spawned > 1) title += " (x" + spawned + ")";
              }
            }
            var p = i < 9 ? ("  " + (i + 1)) : (i + 1).ToString();
            ui.Add(new CuiElement{
                Name = $"{InvName}Items{i}Icon", Parent = $"{InvName}Items",
                Components = {
                    new CuiRawImageComponent{
                        Png = (string.IsNullOrEmpty(cached) || count <= 0)
                                  ? null
                                  : cached,
                        Url = (!string.IsNullOrEmpty(cached) || count <= 0)
                                  ? null
                                  : string.IsNullOrEmpty(logo_link) ? null
                                                                    : logo_link,
                        Sprite =
                            count <= 0
                                ? "assets/content/textures/generic/fulltransparent.tga"
                                : "assets/icons/crate.png",
                    },
                    new CuiRectTransformComponent{
                        AnchorMin = $"0.005 {base_line-0.1f+0.006f}",
                        AnchorMax = $"0.045 {base_line-0.006f}"}}});
            ui.Add(new CuiElement{
                Name = $"{InvName}Items{i}Count", Parent = $"{InvName}Items",
                Components = {
                    new CuiTextComponent{
                        Color = count <= 0 ? InventoryStyleButtonPressedColor
                                           : InventoryStyleButtonTextColor,
                        Text = $"x" + count.ToString().PadRight(4) + " ",
                        Font = font, FontSize = font_size - 4,
                        Align = TextAnchor.MiddleRight},
                    new CuiRectTransformComponent{
                        AnchorMin = $"0 {base_line-0.1f+0.01f}",
                        AnchorMax = $"1 {base_line-0.01f}"}}});
            ui.Add(
                new CuiButton{
                    RectTransform = {AnchorMin = $"0 {base_line-0.1f}",
                                     AnchorMax = $"1 {base_line}"},
                    Button =
                        {
                            Color = "1 1 1 0",
                            Command =
                                count <= 0 ? "" : $"survivalshop#give {i+1}",
                            FadeIn = fade_in,
                        },
                    Text = {Color = count <= 0
                                        ? InventoryStyleButtonPressedColor
                                        : InventoryStyleButtonTextColor,
                            Text = "".PadLeft((int) num_button_paddings) +
                                   $"{title}",
                            Font = font, FontSize = font_size - 4,
                            Align = TextAnchor.MiddleLeft}},
                $"{InvName}Items", $"{InvName}Items{i}Button");
          }
        ui.Add(
            new CuiPanel{
                Image = {Color = InventoryStyleBorderColor,
                         Material = "assets/content/ui/uibackgroundblur.mat",
                         FadeIn = fade_in},
                RectTransform = {AnchorMin = $"0 {1/12f+0.002f}",
                                 AnchorMax = $"1 {1/12f+0.008f}"}},
            $"{InvName}Overlay", $"{InvName}End");
        ui.Add(
            new CuiPanel{
                Image = {Color = InventoryStyleBorderColor,
                         Material = "assets/content/ui/uibackgroundblur.mat",
                         FadeIn = fade_in},
                RectTransform = {AnchorMin = $"0 {1/12f-1/20f}",
                                 AnchorMax = $"0.15 {1/12f}"},
                CursorEnabled = true},
            $"{InvName}Overlay", $"{InvName}Locale");
        ui.Add(
            new CuiButton{
                RectTransform = {AnchorMin = "0.05 0.14",
                                 AnchorMax = "0.475 0.95"},
                Button = {Color = InventoryStyleButtonBackgroundColor,
                          Material = "assets/content/ui/uibackgroundblur.mat",
                          Command = $"survivalshop#ru"},
                Text = {
                  Align = TextAnchor.MiddleCenter,
                  Color = player.Locale == LocaleRu?
                  InventoryStyleButtonPressedColor :
                      InventoryStyleButtonTextColor,
                  Font = font,
                  FontSize = font_size - 8,
                  Text = "RU"
                }},
            $"{InvName}Locale", $"{InvName}Ru");
        ui.Add(
            new CuiButton{
                RectTransform = {AnchorMin = "0.525 0.14",
                                 AnchorMax = "0.95 0.95"},
                Button = {Color = InventoryStyleButtonBackgroundColor,
                          Material = "assets/content/ui/uibackgroundblur.mat",
                          Command = $"survivalshop#en"},
                Text = {
                  Align = TextAnchor.MiddleCenter,
                  Color = player.Locale == LocaleEn?
                  InventoryStyleButtonPressedColor :
                      InventoryStyleButtonTextColor,
                  Font = font,
                  FontSize = font_size - 8,
                  Text = "EN"
                }},
            $"{InvName}Locale", $"{InvName}En");
        if (player.InventoryShowTip) {
          ui.Add(
              new CuiLabel{
                  RectTransform = {AnchorMin = $"0.16 {1/12f-1/20f+0.005f}",
                                   AnchorMax = $"0.21 {1/12f+0.005f}"},
                  Text = {Align = TextAnchor.MiddleRight,
                          Color = InventoryStyleTipColor, Font = font_tip,
                          FontSize = font_size - 10,
                          Text = Locale(player).Tip + " "}},
              $"{InvName}Overlay", $"{InvName}Tip");
          ui.Add(
              new CuiLabel{
                  RectTransform = {AnchorMin = $"0.21 {1/12f-1/20f+0.005f}",
                                   AnchorMax = $"0.6 {1/12f+0.005f}"},
                  Text = {Align = TextAnchor.MiddleLeft,
                          Color = InventoryStyleTipColor, Font = font_tip,
                          FontSize = font_size - 10,
                          Text = Locale(player).QuickBindHelp}},
              $"{InvName}Overlay", $"{InvName}Tip1");
          ui.Add(
              new CuiLabel{
                  RectTransform = {AnchorMin =
                                       $"0.21 {1/12f-1/20f-1/30f+0.005f}",
                                   AnchorMax = $"0.6 {1/12f-1/30f+0.005f}"},
                  Text = {Align = TextAnchor.MiddleLeft,
                          Color = InventoryStyleTipColor, Font = font,
                          FontSize = font_size - 10,
                          Text = Locale(player).QuickBindHelp2}},
              $"{InvName}Overlay", $"{InvName}Tip2");
        }
        ui.Add(
            new CuiPanel{
                Image = {Color = InventoryStyleBorderColor,
                         Material = "assets/content/ui/uibackgroundblur.mat",
                         FadeIn = fade_in},
                RectTransform = {AnchorMin = $"0.6 {1/12f-1/20f}",
                                 AnchorMax = $"0.8 {1/12f}"},
                CursorEnabled = true},
            $"{InvName}Overlay", $"{InvName}Paging");
        ui.Add(
            new CuiButton{
                RectTransform = {AnchorMin = "0.025 0.14",
                                 AnchorMax = "0.15 0.95"},
                Button = {Color = InventoryStyleButtonBackgroundColor,
                          Material = "assets/content/ui/uibackgroundblur.mat",
                          Command = $"survivalshop#prev"},
                Text = {Align = TextAnchor.MiddleCenter,
                        Color = player.InventoryPage >= 1
                                    ? InventoryStyleButtonTextColor
                                    : InventoryStyleButtonPressedColor,
                        Font = font, FontSize = font_size - 8, Text = "«"}},
            $"{InvName}Paging", $"{InvName}PagePrev");
        ui.Add(
            new CuiButton{
                RectTransform = {AnchorMin = "0.175 0.14",
                                 AnchorMax = "0.8 0.95"},
                Button = {Color = "1 1 1 0", Command = $"survivalshop#show"},
                Text = {Align = TextAnchor.MiddleCenter,
                        Color = InventoryStyleButtonTextColor, Font = font,
                        FontSize = font_size - 8,
                        Text = SafeStringFormat(
                            Locale(player).Paging,
                            new object[]{(player.InventoryPage + 1).ToString(),
                                         player.InventoryTotalPages})}},
            $"{InvName}Paging", $"{InvName}PageCurrent");
        ui.Add(
            new CuiButton{
                RectTransform = {AnchorMin = "0.825 0.14",
                                 AnchorMax = "0.95 0.95"},
                Button = {Color = InventoryStyleButtonBackgroundColor,
                          Material = "assets/content/ui/uibackgroundblur.mat",
                          Command = $"survivalshop#next"},
                Text = {
                  Align = TextAnchor.MiddleCenter,
                  Color =
                      (player.InventoryPage + 1) < player.InventoryTotalPages?
                  InventoryStyleButtonTextColor :
                      InventoryStyleButtonPressedColor,
                  Font = font,
                  FontSize = font_size - 8,
                  Text = "»"
                }},
            $"{InvName}Paging", $"{InvName}PageNext");
        ui.Add(new CuiButton{RectTransform = {AnchorMin = "0.8 0.0",
                                              AnchorMax = $"1.0 {1/12f}"},
                             Button = {Color = "0.8 0.28 0.2 1",
                                       Command = $"survivalshop#close"},
                             Text = {Align = TextAnchor.MiddleCenter,
                                     Color = "1 1 1 0.8", Font = font,
                                     FontSize = font_size - 4,
                                     Text = Locale(player).Close}},
               $"{InvName}Overlay", $"{InvName}Close");
        CuiHelper.AddUi(player.Player, ui);
        player.InventoryShown = true;
        player.InventoryTime = DateTime.Now.AddSeconds(10);
      } catch (Exception e) {
        Error(e, "InvDraw");
        Chat(player, player.Locale.FailedToUseInventory);
        throw;
      }
    }
    void InvUndraw(BasePlayer player) {
      CuiHelper.DestroyUi(player, $"{InvName}");
      Player(player).InventoryShown = false;
    }
    void InvShow(BasePlayer base_player, int page_num, bool redraw,
                 bool no_timer) {
      if (page_num < 0) page_num = 0;
      var player = Player(base_player);
      InvDraw(player, false);
      ApiExec(null, "client.getInventory",
              new Dictionary<string, object>(){
                  {"siteId", SiteId},
                  {"clientSid", player.PlayerId},
                  {"criteria", new {_start = page_num * InventoryItemsPerPage,
                                    _limit = InventoryItemsPerPage}}},
              (r) => {
                player.Inventory = (JArray) r.Data ["response"]
                                   ["result"];
                player.InventoryCount = (int) r.Data ["response"]
                                        ["count"];
                int total_pages =
                    player.InventoryCount < InventoryItemsPerPage? 1
                    : (int) Math.Ceiling(player.InventoryCount /
                                         (float) InventoryItemsPerPage);
                if (page_num >= total_pages) page_num = total_pages - 1;
                player.InventoryPage = page_num;
                player.InventoryTotalPages = total_pages;
                player.InventoryNoTimer = no_timer;
                InvDraw(player, true);
              },
              (r, e) => ApiFailed(player, r, e));
    }
    HashSet<string> PrefabList = new HashSet<string>(){
        "vehicle",
    };
    Vector3 GetFixedPositionForPlayer(BasePlayer player, float ofs_forward = 4f,
                                      float ofs_up = 0.02f) {
      Vector3 forward = player.GetNetworkRotation() * Vector3.forward;
      forward.y = 0;
      return player.transform.position + forward.normalized * ofs_forward +
             Vector3.up * ofs_up;
    }
    Quaternion GetFixedRotationForPlayer(BasePlayer player) {
      return Quaternion.Euler(
          0, player.GetNetworkRotation().eulerAngles.y - 135, 0);
    }
    Vector3 DropToFloor(Vector3 pos) {
      RaycastHit hit;
      pos.y = UnityEngine.Physics.Raycast(pos + Vector3.up * 5f, Vector3.down,
                                          out hit, 50f,
                                          Layers.Solid | Layers.Mask.Water)
                  ? hit.point.y
                  : TerrainMeta.HeightMap.GetHeight(pos);
      return pos;
    }
    Vector3 GetNearestCenterOfFloorForPlayer(BasePlayer player,
                                             bool drop_to_floor = true) {
      var blocks = Facepunch.Pool.GetList<BuildingBlock>();
      var pos = GetFixedPositionForPlayer(player);
      Vis.Entities(pos, 2f, blocks, Layers.Mask.Construction);
      if (blocks.Count > 0) {
        var position = pos;
        var closest =
            blocks.Where(x => !x.ShortPrefabName.Contains("wall"))
                .OrderBy(x =>(x.transform.position - position).magnitude)
                .FirstOrDefault();
        if (closest != null) {
          var wsb = closest.WorldSpaceBounds();
          pos = wsb.position;
          pos.y += wsb.extents.y;
          Facepunch.Pool.FreeList(ref blocks);
          if (drop_to_floor) pos = DropToFloor(pos);
          return pos;
        }
      }
      Facepunch.Pool.FreeList(ref blocks);
      pos = GetFixedPositionForPlayer(player);
      if (drop_to_floor) pos = DropToFloor(pos);
      return pos;
    }
    string InvDeliverCheck(PlayerData player, JObject equip, string errors) {
      var count = (int) equip["count"];
      var info = (JObject) equip["info"];
      var type = (string) info["type"];
      var class_name = (string) info["className"];
      var bp_path = (string) info["bpPath"];
      if (type == "cmd") {
        return errors;
      }
      if (type == "item") {
        var item = ItemManager.FindItemDefinition(bp_path);
        if (item == null) {
          errors += (!string.IsNullOrEmpty(errors) ? ", " : "") +
                    SafeStringFormat(player.Locale.ItemNotFound,
                                     new object[]{bp_path});
          return errors;
        }
        var inv = player.Player.inventory;
        var inv_capacity =
            inv.containerMain.capacity + inv.containerBelt.capacity;
        var stack_size = item.stackable > 1 ? item.stackable : 1;
        var stack_slots = (int) Math.Ceiling((double) count / stack_size);
        var after_slots = inv.containerMain.itemList.Count +
                          inv.containerBelt.itemList.Count + stack_slots;
        if (after_slots > inv_capacity) {
          errors += (!string.IsNullOrEmpty(errors) ? ", " : "") +
                    SafeStringFormat(player.Locale.RequireMoreSlots,
                                     new object[]{after_slots - inv_capacity});
          return errors;
        }
        return errors;
      }
      if (type == "prefab") {
        if (player.Player.IsBuildingBlocked()) {
          errors += (!string.IsNullOrEmpty(errors) ? ", " : "") +
                    player.Locale.BuildingBlockedHere;
          return errors;
        }
        if (!PrefabList.Contains(class_name)) {
          errors += (!string.IsNullOrEmpty(errors) ? ", " : "") +
                    "wrong prefab class: " + class_name;
          return errors;
        }
        return errors;
      }
      errors += (!string.IsNullOrEmpty(errors) ? ", " : "") +
                "wrong equip type: '" + type + "'";
      return errors;
    }
    int InvDeliver(PlayerData player, JObject equip, JObject slot,
                   ref string log_message, ref string log_error_message,
                   ItemAffixes affixes) {
      var info = (JObject) equip["info"];
      var type = (string) info["type"];
      var name = (string) info["name"];
      var class_name = (string) info["className"];
      var bp_path = (string) info["bpPath"];
      var sale = slot ["meta"]
      ["_sale"] as JObject;
      var case_h = slot ["meta"]
      ["_case_h"] as JObject;
      var price = 0L;
      if (sale != null)
        price = (long) sale["price"];
      else if (case_h != null)
        price = (long) case_h["price"];
      if (type == "cmd") {
        CmdDeliver(bp_path, player, (string) slot["title"], (int) slot["count"],
                   price, (string) slot["_id"], ref log_message,
                   ref log_error_message);
        return 1;
      }
      if (type == "item") {
        var count = (int) equip["count"];
        var item = ItemManager.FindItemDefinition(bp_path);
        if (item == null) {
          log_error_message += "item '" + bp_path + "' not found";
          return 0;
        }
        var inv = player.Player.inventory;
        var inv_capacity =
            inv.containerMain.capacity + inv.containerBelt.capacity;
        var stack_size = item.stackable > 1 ? item.stackable : 1;
        var stack_slots = (int) Math.Ceiling((double) count / stack_size);
        var after_slots = inv.containerMain.itemList.Count +
                          inv.containerBelt.itemList.Count + stack_slots;
        if (after_slots > inv_capacity) {
          log_error_message +=
              "need more slots: " + (after_slots - inv_capacity);
          return 0;
        }
        if (affixes.IsBlueprint) {
          player.Player.blueprints.Unlock(item);
          log_message += "blueprint unlocked!";
        } else {
          log_message += "x" + count + " (stack " + stack_size + ")";
          for (var i = count; i > 0; i -= stack_size) {
            var gave = ItemManager.Create(item, i >= stack_size? stack_size
                                          : i, 0);
            player.Player.GiveItem(gave);
            if (gave.info.shortname == "smallwaterbottle" &&
                gave.contents != null) {
              var water = ItemManager.CreateByName("water", 250);
              water?.MoveToContainer(gave.contents);
            }
          }
        }
        return count;
      }
      if (type == "prefab") {
        if (class_name == "vehicle") {
          var entity = GameManager.server.CreateEntity(
              bp_path, GetNearestCenterOfFloorForPlayer(player.Player),
              GetFixedRotationForPlayer(player.Player));
          if (entity == null) {
            log_error_message += "failed to create entity!";
            return 0;
          }
          entity.OwnerID = player.Player.userID;
          entity.Spawn();
          log_message += "vehicle spawned ";
          var vehicle = entity as BaseVehicle;
          if (vehicle != null) {
            var fueled = AutofuelEnabled && (bool) info["fueled"];
            if (!fueled)
              log_message += "(unfueled) ";
            else {
              log_message += "(fueled) ";
              var fs = vehicle.GetFuelSystem();
              if (fs != null) fs.FillFuel();
            }
          }
          return 1;
        }
        log_error_message += "wrong prefab class: '" + class_name + "'";
        return 0;
      }
      log_error_message += "wrong equip type: '" + type + "'";
      return 0;
    }
    int CmdDeliver(PlayerData player, JObject cmd, JObject slot,
                   ref string log_message, ref string log_error_message) {
      if (cmd == null) throw new Exception("Command object is NULL");
      var sale = slot ["meta"]
      ["_sale"] as JObject;
      var case_h = slot ["meta"]
      ["_case_h"] as JObject;
      var price = 0L;
      if (sale != null)
        price = (long) sale["price"];
      else if (case_h != null)
        price = (long) case_h["price"];
      var command = (string) cmd["raw"];
      CmdDeliver(command, player, (string) slot["title"], (int) slot["count"],
                 price, (string) slot["_id"], ref log_message,
                 ref log_error_message);
      return 1;
    }
    int CmdDeliver(string command, PlayerData player, string title, int count,
                   long price, string id, ref string log_message,
                   ref string log_error_message) {
      if (command == null) throw new Exception("Command is NULL");
      command = command.Replace("{player.sid}", player.PlayerId);
      command = command.Replace("{player.name}", player.Player.displayName);
      command = command.Replace("{item.name}", title);
      command = command.Replace("{item.count}", count.ToString());
      command = command.Replace("{item.price}", price.ToString());
      command = command.Replace("{item.id}", id);
      ConsoleSystem.Run(ConsoleSystem.Option.Server.Quiet(), command);
      log_message += "cmd (" + command + ") ";
      return 1;
    }
    string InvGiveCheck(PlayerData player, JObject slot) {
      if (player.Player == null || player.Player.Connection == null ||
          player.Player.IsDead() || player.Player.IsWounded() ||
          player.Player.IsSleeping() || player.Player.IsSpectating())
        return player.Locale.YourCharacterMustBeConcious;
      var errors = (string) null;
      var equips = (JArray) slot ["content"]
      ["equips"];
      if (equips != null)
        for (int i = 0; i < equips.Count; i++)
          errors = InvDeliverCheck(player, (JObject) equips[i], errors);
      return errors;
    }
    void InvGive(PlayerData player, JObject slot, int quantity) {
      var title = (string) slot["title"];
      var affixes = new ItemAffixes(title);
      var player_message = title;
      var error_message = "";
      var log_message = SafeStringFormat(
          Locale().Purshase_0_for_1_,
          new object[]{(string) slot["title"], player.PlayerId});
      var spawned = 0;
      try {
        var equips = (JArray) slot ["content"]
        ["equips"];
        if (equips != null)
          for (int q = quantity; q > 0; q--)
            for (int i = 0; i < equips.Count; i++)
              if (equips[i] != null)
                spawned +=
                    InvDeliver(player, (JObject) equips[i], slot,
                               ref log_message, ref error_message, affixes);
        var cmds = (JArray) slot ["content"]
        ["cmds"];
        if (cmds != null)
          for (int q = quantity; q > 0; q--)
            for (int i = 0; i < cmds.Count; i++)
              if (cmds[i] != null)
                spawned += CmdDeliver(player, (JObject) cmds[i], slot,
                                      ref log_message, ref error_message);
      } catch (Exception e) {
        error_message = e.Message + " - " + e.StackTrace;
        if (DebugEnabled) {
          error_message += ", player is ";
          if (player == null)
            error_message += "null";
          else
            error_message += player.PlayerId;
          error_message += ", slot is ";
          if (slot == null)
            error_message += "null";
          else
            error_message += JsonConvert.SerializeObject(slot, Formatting.None);
        }
      }
      if (spawned > 1) player_message += " (" + spawned + ")";
      if (string.IsNullOrEmpty(error_message)) {
        if (affixes.IsBlueprint)
          Chat(player, player.Locale.Unlocked_0_1, player_message, "");
        else
          Chat(player, player.Locale.Delivered_0_1, player_message, "");
        Notify(log_message);
      } else {
        if (affixes.IsBlueprint)
          Chat(player, player.Locale.CannotUnlock_0_1, player_message,
               player.Locale.UnlockError);
        else
          Chat(player, player.Locale.CannotDeliver_0_1, player_message,
               player.Locale.DeliveryError);
        Notify(log_message);
        Error(error_message);
      }
    }
    void InvTakeAndGive(BasePlayer base_player, int position,
                        int quantity = -1) {
      var player = Player(base_player);
      if (player == null) {
        Chat(player, player.Locale.PlayerNotFound);
        return;
      }
      if (player.Inventory == null) {
        Chat(player, player.Locale.ShopUsage, "/shop");
        return;
      }
      if (position < 0 || position >= player.Inventory.Count) {
        Chat(player, player.Locale.NoSuchPosition);
        return;
      }
      var slot_id = (string) player.Inventory [position]
                    ["_id"];
      InvTakeAndGiveSlot(player, slot_id, position, true, quantity);
    }
    void ApiItemsActivate(ApiResponse r, JObject slot, PlayerData player,
                          string slot_id, int position, bool redraw_gui,
                          int quantity) {
      InvGive(player, slot, quantity);
      if (position >= 0)
        player.Inventory [position]
        ["count"] = (int) player.Inventory [position]
                    ["count"] -
                    quantity;
      if (redraw_gui) InvDraw(player, true);
    }
    void ApiGetInventorySlot(ApiResponse r, PlayerData player, string slot_id,
                             int position, bool redraw_gui, int quantity) {
      JObject slot = null;
      try {
        if (r == null) throw new Exception("Response is null");
        if (r.Data == null) throw new Exception("Response data is null");
        slot = r.Data["response"] as JObject;
        if (slot == null) throw new Exception("Response content is null");
        if (quantity < 0)
          quantity = (int) slot["count"];
        else if ((int) slot["count"] < quantity) {
          Chat(player, player.Locale.CountNotEnough);
          return;
        }
        var error = InvGiveCheck(player, slot);
        if (!string.IsNullOrEmpty(error)) {
          Chat(player, player.Locale.CannotDeliver_0_1, (string) slot["title"],
               error);
          return;
        }
        ApiExec(null, "items.activate",
                new Dictionary<string, object>(){{"siteId", SiteId},
                                                 {"clientSid", player.PlayerId},
                                                 {"slotId", slot_id},
                                                 {"quantity", quantity}},
                (a) => ApiItemsActivate(a, slot, player, slot_id, position,
                                        redraw_gui, quantity),
                (a, e) => ApiFailed(player, a, e));
      } catch (Exception e) {
        Chat(player, player.Locale.FailedToUseSlot);
        throw new Exception(
            "InvTakeAndGiveSlot failed for " + player.PlayerId +
                ", slot: " + slot_id +
                (slot != null?(", " + JsonConvert.SerializeObject(
                                          slot, Formatting.None))
                 : r != null && r.ResponseString != null? r.ResponseString
                 : ""),
            e);
      }
    }
    void InvTakeAndGiveSlot(PlayerData player, string slot_id,
                            int position = -1, bool redraw_gui = false,
                            int quantity = -1) {
      if (player == null) return;
      ApiExec(null, "client.getInventorySlot",
              new Dictionary<string, object>(){{"siteId", SiteId},
                                               {"clientSid", player.PlayerId},
                                               {"slotId", slot_id}},
              (r) => ApiGetInventorySlot(r, player, slot_id, position,
                                         redraw_gui, quantity),
              (r, e) => ApiFailed(player, r, e));
    }
    void InvFrame() {
      if (DateTime.Now < NextTimerUpdate) return;
      foreach (var kv in Players)
        if (kv.Value.InventoryTime > DateTime.MinValue &&
            kv.Value.InventoryTime < DateTime.Now &&
            !kv.Value.InventoryNoTimer) {
          InvUndraw(kv.Value.Player);
          kv.Value.InventoryTime = DateTime.MinValue;
        }
      NextTimerUpdate = DateTime.Now.AddSeconds(1);
    }
    void InvUnload() { InvStopAll(); }
    string InvLinkName = "SurvivalshopLinkCUI";
    void InvLinkDraw(BasePlayer player) {
      InvLinkUndraw(player);
      var cui = new CuiElementContainer();
      cui.Add(
          new CuiButton{
              RectTransform = {AnchorMin = "0 1", AnchorMax = "0 1",
                               OffsetMin = "10 -32", OffsetMax = "36 -6"},
              Button = {Color = "1 1 1 0.6", Sprite = "assets/icons/loot.png",
                        Command = "chat.say /shopinv"},
              Text = {Text = ""}},
          "Overlay", InvLinkName);
      CuiHelper.AddUi(player, cui);
    }
    void InvLinkUndraw(BasePlayer player) {
      CuiHelper.DestroyUi(player, InvLinkName);
    }
    DateTime NextPowerupUpdate;
    void PowerupsAcquire(JArray powerups) {
      var ids = "";
      try {
        if (powerups == null) return;
        ThreadLock("Powerup activations", () => {
          for (int i = 0; i < powerups.Count; i++) {
            var powerup = (JObject) powerups[i];
            var id = (string) powerup["_id"];
            if (id != null && !PowerupActivations.ContainsKey(id)) {
              PowerupActivations[id] = powerup;
              ids += (ids != "" ? ", " : "") + id;
            }
          }
        }, ref PowerupActivationsLock, PowerupActivations);
      } catch (Exception e) {
        Error(e, "PowerupsAcquire");
      }
      if (ids != "") Plugin.Notify(Locale().AcquiredPowerups, ids);
    }
    void PowerupFrame() {
      if (DateTime.Now < NextPowerupUpdate) return;
      if (string.IsNullOrEmpty(ShopServerId) || string.IsNullOrEmpty(SiteId))
        return;
      ThreadLock("Powerup activations", () => {
        var one = "";
        foreach (var kv in PowerupActivations) {
          one = kv.Key;
          break;
        }
        if (!string.IsNullOrEmpty(one)) {
          PowerupTakeAndGive(PowerupActivations[one]);
          PowerupActivations.Remove(one);
        }
      }, ref PowerupActivationsLock, PowerupActivations);
      NextPowerupUpdate = DateTime.Now.AddSeconds(1);
    }
    void PowerupDeliverEquip(string steam_id, string steam_name,
                             PlayerData player_online, JObject equip,
                             JObject slot, ref string log_message,
                             ref string log_error_message,
                             ItemAffixes affixes) {
      var info = (JObject) equip["info"];
      var type = (string) info["type"];
      var bp_path = (string) info["bpPath"];
      if (type == "cmd") {
        PowerupDeliverCmd(bp_path, steam_id, steam_name, player_online,
                          (string) slot["title"], ref log_message,
                          ref log_error_message);
        return;
      }
      log_error_message += "wrong equip type: '" + type + "'";
    }
    int PowerupDeliverCmd(string steam_id, string steam_name,
                          PlayerData player_online, JObject cmd, JObject slot,
                          ref string log_message,
                          ref string log_error_message) {
      if (cmd == null) throw new Exception("Command object is NULL");
      var command = (string) cmd["raw"];
      PowerupDeliverCmd(command, steam_id, steam_name, player_online,
                        (string) slot["title"], ref log_message,
                        ref log_error_message);
      return 1;
    }
    int PowerupDeliverCmd(string command, string steam_id, string steam_name,
                          PlayerData player_online, string title,
                          ref string log_message,
                          ref string log_error_message) {
      if (command == null) throw new Exception("Command is NULL");
      command = command.Replace("{player.sid}", steam_id);
      command =
          command.Replace("{player.name}", player_online == null? "undefined"
                          : player_online.Player.displayName);
      command = command.Replace("{item.name}", title);
      command = command.Replace("{item.count}", "1");
      command = command.Replace("{item.price}", "1");
      command = command.Replace("{item.id}", "undefined");
      ConsoleSystem.Run(ConsoleSystem.Option.Server.Quiet(), command);
      log_message += "Cmd: " + command + ". ";
      return 1;
    }
    void ApiPowerupGive(JObject slot) {
      var player_message = "";
      var error_message = "";
      var log_message = "";
      var player_online = (PlayerData) null;
      try {
        var title = (string) slot["title"];
        var steam_id = (string) slot ["_client"]
        ["sid"];
        var steam_name = (string) slot ["_client"]
        ["username"];
        var affixes = new ItemAffixes(title);
        player_online = Player(BasePlayer.Find(steam_id));
        player_message = title;
        log_message = SafeStringFormat(Locale().PowerupDelivered_for_0_1,
                                       new object[]{steam_id, title + " "});
        var equips = (JArray) slot ["content"]
        ["equips"];
        if (equips != null)
          for (int i = 0; i < equips.Count; i++)
            if (equips[i] != null)
              PowerupDeliverEquip(steam_id, steam_name, player_online,
                                  (JObject) equips[i], slot, ref log_message,
                                  ref error_message, affixes);
        var cmds = (JArray) slot ["content"]
        ["cmds"];
        if (cmds != null)
          for (int i = 0; i < cmds.Count; i++)
            if (cmds[i] != null)
              PowerupDeliverCmd(steam_id, steam_name, player_online,
                                (JObject) cmds[i], slot, ref log_message,
                                ref error_message);
      } catch (Exception e) {
        error_message = e.Message + " - " + e.StackTrace;
      }
      Notify(log_message);
      if (!string.IsNullOrEmpty(error_message)) Error(error_message);
      if (player_online == null) return;
      if (string.IsNullOrEmpty(error_message))
        Chat(player_online, player_online.Locale.Activated_0_1, player_message,
             "");
      else
        Chat(player_online, player_online.Locale.CannotActivate_0_1,
             player_message, player_online.Locale.ActivationError);
      if (slot["_slot"] != null)
        InvTakeAndGiveSlot(player_online, (string) slot["_slot"]);
    }
    void PowerupTakeAndGive(JObject slot) {
      ApiExec(
          null, "servers.activatePowerup",
          new Dictionary<string, object>(){{"siteId", SiteId},
                                           {"serverId", ShopServerId},
                                           {"powerupId", (string) slot["_id"]}},
          (r) => ApiPowerupGive((JObject) r.Data["response"]),
          (r, e) => ApiFailed(null, r, e));
    }
    Dictionary<string, string> DownloadedImages =
        new Dictionary<string, string>();
    Queue<string> DownloadingImages = new Queue<string>();
    bool IsDownloadingImage;
    internal void DownloadNextImage() {
      if (DownloadingImages.Count == 0) {
        IsDownloadingImage = false;
        return;
      }
      Rust.Global.Runner.StartCoroutine(
          DownloadImage(DownloadingImages.Dequeue()));
      IsDownloadingImage = true;
    }
    internal string StoreImage(string url, byte[] png_data) {
      var id =
          FileStorage.server.Store(png_data, FileStorage.Type.png,
                                   CommunityEntity.ServerInstance.net.ID, 0);
      if (FileStorage.server.Get(id, FileStorage.Type.png,
                                 CommunityEntity.ServerInstance.net.ID,
                                 0) == null) {
        Error("Failed to store image {0} to server database", url);
        return null;
      }
      DownloadedImages[url] = id.ToString();
      Notify("Downloaded {0} to server database as {1}", url, id);
      return id.ToString();
    }
    System.Collections.IEnumerator DownloadImage(string url) {
      var get = UnityWebRequestTexture.GetTexture(url);
      yield return get.SendWebRequest();
      if (!String.IsNullOrEmpty(get.error))
        Error("Download image {0}: {1} (make sure image format is supported)",
              url, get.error);
      else {
        var tex = DownloadHandlerTexture.GetContent(get);
        if (tex == null || tex.height == 8 && tex.width == 8 &&
                               tex.name == string.Empty && tex.anisoLevel == 1)
          Error("Download image {0}: No data received", url);
        else
          StoreImage(url, get.downloadHandler.data);
      }
      DownloadNextImage();
    }
    internal string GetImage(string url, out string cached,
                             bool only_cached = false) {
      if (string.IsNullOrWhiteSpace(url) ||
          !url.EndsWith(".png", StringComparison.InvariantCultureIgnoreCase)) {
        cached = null;
        return null;
      }
      if (!url.StartsWith("http://",
                          StringComparison.InvariantCultureIgnoreCase) &&
          !url.StartsWith("https://",
                          StringComparison.InvariantCultureIgnoreCase))
        url = ApiRequest.ImageUrl + url;
      string img;
      if (DownloadedImages.TryGetValue(url, out img)) {
        cached = img;
        return img;
      }
      cached = null;
      DownloadingImages.Enqueue(url);
      if (!IsDownloadingImage) DownloadNextImage();
      return only_cached? null : url;
    }
    void StoreCmd(BasePlayer base_player, string command, string[] args) {
      if (Auto) return;
      try {
        if (!ShopRegistered) {
          Chat(base_player, Locale(base_player).ShopNotRegistered);
          return;
        }
        int page_num = 0;
        try {
          if (args.Length == 1)
            page_num = int.Parse(args[0]) - 1;
          else if (args.Length != 0)
            throw new Exception("Bad args count");
        } catch {
          Chat(base_player, Locale(base_player).ShopUsage, command);
          return;
        }
        var player = Player(base_player);
        if (command.Contains("invt")) {
          if (player.InventoryShown)
            InvUndraw(base_player);
          else
            InvShow(base_player, page_num, false, true);
          player.InventoryShowTip = false;
          player.InventoryShowTipCounter = -1;
        } else {
          if (player.InventoryShowTipCounter >= 0)
            player.InventoryShowTipCounter++;
          if (player.InventoryShowTipCounter > 10)
            player.InventoryShowTip = false;
          else if (player.InventoryShowTipCounter > 5)
            player.InventoryShowTip = true;
          InvShow(base_player, page_num, false, command.Contains("inv"));
        }
      } catch (Exception e) {
        Error(Locale().InventoryFailedToShow, e.Message,
              command + " " + string.Join(" ", args));
      }
    }
    void GiveCmd(BasePlayer base_player, string command, string[] args) {
      if (Auto) return;
      try {
        if (!ShopRegistered) {
          Chat(base_player, Locale(base_player).ShopNotRegistered);
          return;
        }
        int position = 0;
        int quantity = 1;
        try {
          if (args.Length == 1)
            position = int.Parse(args[0]) - 1;
          else if (args.Length == 2) {
            position = int.Parse(args[0]) - 1;
            quantity = int.Parse(args[1]);
            if (quantity <= 0) throw new Exception("Bad quantity");
          } else
            throw new Exception("Bad args count");
        } catch {
          Chat(base_player, Locale(base_player).ShopGiveUsage, command);
          return;
        }
        InvTakeAndGive(base_player, position, quantity);
      } catch (Exception e) {
        Error(Locale().InventoryFailedToTake, e.Message,
              command + " " + string.Join(" ", args));
      }
    }
    void GiveAllCmd(BasePlayer base_player, string command, string[] args) {
      if (Auto) return;
      try {
        if (!ShopRegistered) {
          Chat(base_player, Locale(base_player).ShopNotRegistered);
          return;
        }
        int position = 0;
        try {
          if (args.Length == 1)
            position = int.Parse(args[0]) - 1;
          else
            throw new Exception("Bad args count");
        } catch {
          Chat(base_player, Locale(base_player).ShopGiveUsage, command);
          return;
        }
        InvTakeAndGive(base_player, position, -1);
      } catch (Exception e) {
        Error(Locale().InventoryFailedToTakeAll, e.Message,
              command + " " + string.Join(" ", args));
      }
    }
    void LocaleEnCmd(BasePlayer base_player, string command, string[] args) {
      if (Auto) return;
      try {
        LocaleSet(Player(base_player), LocaleEn);
      } catch (Exception e) {
        Error(Locale().LocaleFailed, e.Message,
              command + " " + string.Join(" ", args));
      }
    }
    void LocaleRuCmd(BasePlayer base_player, string command, string[] args) {
      if (Auto) return;
      try {
        LocaleSet(Player(base_player), LocaleRu);
      } catch (Exception e) {
        Error(Locale().LocaleFailed, e.Message,
              command + " " + string.Join(" ", args));
      }
    }
    bool InfoCmd(ConsoleSystem.Arg arg) {
      Notify("{0} ({1}) (c) {2}", "SurvivalShop", "2.7.0",
             "SurvivalShop.org, clickable GUI by Sth");
      Notify(Locale().Status01, ShopRegistered, SiteId, ServerInitialized,
             DebugEnabled, AutofuelEnabled, (ApiKey + "").Length,
             NextRegister.ToString());
      Notify(Locale().Status02, ShopServerId, ShopHello, ThreadLockState,
             ThreadLockCounter, Auto);
      Notify(Locale().Status03, Locale().Locale);
      Notify(Locale().Status04, Players.Count);
      int numRequests = 0;
      foreach (var kv in ApiRequests) numRequests += kv.Value.Count;
      Notify(Locale().Status05, numRequests);
      return true;
    }
    bool OptionCmd(ConsoleSystem.Arg arg, string option) {
      if (arg == null || arg.Args == null || arg.Args.Length != 1) {
        Notify(Locale().OptionUsage, arg.cmd.Name);
        return true;
      }
      if (SetupBool(option, arg.Args[0]))
        Notify(Locale().OptionIsNowOn, option);
      else
        Notify(Locale().OptionIsNowOn, option);
      return true;
    }
    bool DebugCmd(ConsoleSystem.Arg arg) { return OptionCmd(arg, "debug"); }
    bool TranCmd(ConsoleSystem.Arg arg) { return OptionCmd(arg, "useTran"); }
    bool AutoCmd(ConsoleSystem.Arg arg) { return OptionCmd(arg, "auto"); }
    bool AutofuelCmd(ConsoleSystem.Arg arg) {
      return OptionCmd(arg, "autofuel");
    }
    bool NoWelcomeCmd(ConsoleSystem.Arg arg) {
      return OptionCmd(arg, "noWelcome");
    }
    bool ReloadCmd(ConsoleSystem.Arg arg) {
      LoadConfig();
      return true;
    }
    bool SetupCmd(ConsoleSystem.Arg arg) {
      if (arg.Args.Length == 2) {
        SetupConfig(arg.Args[0], arg.Args[1], null);
        Notify(Locale().SetupOk);
      } else if (arg.Args.Length == 3) {
        SetupConfig(arg.Args[0], arg.Args[1], arg.Args[2]);
        Notify(Locale().SetupOk);
      } else
        Notify(Locale().ShopSetupUsage, arg.cmd.Name);
      return true;
    }
    bool ListItems(ConsoleSystem.Arg arg) {
      Notify("--- Item Definitions ---");
      var list = ItemManager.GetItemDefinitions();
      for (int i = 0; i < list.Count; i++) {
        var item = list[i];
        Notify(item.itemid.ToString().PadLeft(12) + " " + item.Blueprint.name +
               " '" + item.name + "'");
      }
      Notify("--- End ---");
      return true;
    }
    [ConsoleCommand("survivalshop#close")]
    void GuiClose(ConsoleSystem.Arg arg) {
      if (Auto) return;
      var p = Player(arg.Player());
      InvUndraw(p.Player);
    }
    [ConsoleCommand("survivalshop#ru")]
    void GuiRU(ConsoleSystem.Arg arg) {
      if (Auto) return;
      var p = Player(arg.Player());
      LocaleSet(p, LocaleRu);
      InvShow(p.Player, p.InventoryPage, true, p.InventoryNoTimer);
    }
    [ConsoleCommand("survivalshop#en")]
    void GuiEN(ConsoleSystem.Arg arg) {
      if (Auto) return;
      var p = Player(arg.Player());
      LocaleSet(p, LocaleEn);
      InvShow(p.Player, p.InventoryPage, true, p.InventoryNoTimer);
    }
    [ConsoleCommand("survivalshop#next")]
    void GuiNextPage(ConsoleSystem.Arg arg) {
      if (Auto) return;
      var p = Player(arg.Player());
      if (p.InventoryPage < (p.InventoryTotalPages - 1)) p.InventoryPage++;
      InvShow(p.Player, p.InventoryPage, true, p.InventoryNoTimer);
    }
    [ConsoleCommand("survivalshop#show")]
    void GuiShow(ConsoleSystem.Arg arg) {
      if (Auto) return;
      Player(arg.Player()).Player.SendConsoleCommand("chat.say \"/sgui\"");
    }
    [ConsoleCommand("survivalshop#prev")]
    void GuiPrevPage(ConsoleSystem.Arg arg) {
      if (Auto) return;
      var p = Player(arg.Player());
      if (p.InventoryPage > 0) p.InventoryPage--;
      InvShow(p.Player, p.InventoryPage, true, p.InventoryNoTimer);
    }
    [ConsoleCommand("survivalshop#give")]
    void GuiTakeAll(ConsoleSystem.Arg arg) {
      if (Auto) return;
      var p = Player(arg.Player());
      int pos = Convert.ToInt32(arg.GetString(0)) - 1;
      InvTakeAndGive(p.Player, pos, 1);
    }
    void OnServerInitialized() {
      try {
        Notify("========== LOADING ==========");
        permission.RegisterPermission(PermissionSkipQueue, this);
        ApiInit();
        LocaleInit();
        InvInit();
        LoadConfig();
        var commands = GetLibrary<Command>();
        commands.AddConsoleCommand("survivalshop-info", this, InfoCmd);
        commands.AddConsoleCommand("survivalshop-status", this, InfoCmd);
        commands.AddConsoleCommand("survivalshop-state", this, InfoCmd);
        commands.AddConsoleCommand("survivalshop-debug", this, DebugCmd);
        commands.AddConsoleCommand("survivalshop-autofuel", this, AutofuelCmd);
        commands.AddConsoleCommand("survivalshop-auto", this, AutoCmd);
        commands.AddConsoleCommand("survivalshop-nowelcome", this,
                                   NoWelcomeCmd);
        commands.AddConsoleCommand("survivalshop-items", this, ListItems);
        commands.AddConsoleCommand("survivalshop-reload", this, ReloadCmd);
        commands.AddConsoleCommand("survivalshop-setup", this, SetupCmd);
        commands.AddConsoleCommand("survivalshop-tran", this, TranCmd);
        commands.AddConsoleCommand("shop-info", this, InfoCmd);
        commands.AddConsoleCommand("shop-status", this, InfoCmd);
        commands.AddConsoleCommand("shop-state", this, InfoCmd);
        commands.AddConsoleCommand("shop-debug", this, DebugCmd);
        commands.AddConsoleCommand("shop-autofuel", this, AutofuelCmd);
        commands.AddConsoleCommand("shop-auto", this, AutoCmd);
        commands.AddConsoleCommand("shop-nowelcome", this, NoWelcomeCmd);
        commands.AddConsoleCommand("shop-items", this, ListItems);
        commands.AddConsoleCommand("shop-reload", this, ReloadCmd);
        commands.AddConsoleCommand("shop-setup", this, SetupCmd);
        commands.AddConsoleCommand("shop-tran", this, TranCmd);
        commands.AddConsoleCommand("survivalshop.setup", this, SetupCmd);
        commands.AddConsoleCommand("survivalshop.reload", this, ReloadCmd);
        commands.AddConsoleCommand("survivalshop.status", this, InfoCmd);
        commands.AddConsoleCommand("survivalshop.debug", this, DebugCmd);
        commands.AddConsoleCommand("survivalshop.autofuel", this, AutofuelCmd);
        commands.AddConsoleCommand("survivalshop.auto", this, AutoCmd);
        commands.AddConsoleCommand("survivalshop.nowelcome", this,
                                   NoWelcomeCmd);
        commands.AddConsoleCommand("survivalshop.items", this, ListItems);
        commands.AddConsoleCommand("survivalshop.tran", this, TranCmd);
        if (!Auto) {
          commands.AddChatCommand("store", this, StoreCmd);
          commands.AddChatCommand("shop", this, StoreCmd);
          commands.AddChatCommand("shopinv", this, StoreCmd);
          commands.AddChatCommand("shopinvt", this, StoreCmd);
          commands.AddChatCommand("give", this, GiveCmd);
          commands.AddChatCommand("take", this, GiveCmd);
          commands.AddChatCommand("магазин", this, StoreCmd);
          commands.AddChatCommand("магаз", this, StoreCmd);
          commands.AddChatCommand("м", this, StoreCmd);
          commands.AddChatCommand("v", this, StoreCmd);
          commands.AddChatCommand("шоп", this, StoreCmd);
          commands.AddChatCommand("взять", this, GiveCmd);
          commands.AddChatCommand("в", this, GiveCmd);
          commands.AddChatCommand("d", this, StoreCmd);
          commands.AddChatCommand("giveall", this, GiveAllCmd);
          commands.AddChatCommand("takeall", this, GiveAllCmd);
          commands.AddChatCommand("взятьвсе", this, GiveAllCmd);
          commands.AddChatCommand("вв", this, GiveAllCmd);
          commands.AddChatCommand("en", this, LocaleEnCmd);
          commands.AddChatCommand("ru", this, LocaleRuCmd);
          commands.AddChatCommand("sgui", this, StoreCmd);
          commands.AddChatCommand("givegui", this, GiveCmd);
          commands.AddChatCommand("giveallgui", this, GiveAllCmd);
        }
      } catch (Exception e) {
        Error(e, "OnServerInitialized");
      }
      ServerInitialized = true;
    }
    void OnPlayerDisconnected(BasePlayer player, string reason) {
      if (!ServerInitialized) return;
      try {
        Debug("Player {0} disconnected", player.UserIDString);
        Players.Remove(player.UserIDString);
      } catch (Exception e) {
        Error(e, "OnPlayerDisconnected");
      }
    }
    void OnPlayerConnected(BasePlayer player) {
      if (!ServerInitialized) return;
      try {
        Debug("Player {0} connected", player.UserIDString);
        if (ShopRegistered) InvStart(player);
      } catch (Exception e) {
        Error(e, "OnPlayerConnected");
      }
    }
    object CanBypassQueue(Network.Connection connection) {
      if (ConVar.Server.maxplayers == 0) return null;
      var steam_id = connection.userid.ToString();
      if (permission.UserHasPermission(steam_id, PermissionSkipQueue) == true) {
        Debug("Bypassing queue for connecting player {0}", steam_id);
        return true;
      }
      return null;
    }
    void OnTick() {
      if (!ServerInitialized) return;
      var state = "ApiTick";
      try {
        ApiTick();
        if (DateTime.Now > NextTickFrame) {
          NextTickFrame = DateTime.Now.AddSeconds(1.5f);
          state = "ApiFrame";
          ApiFrame();
          state = "InvFrame";
          InvFrame();
          state = "PowerupFrame";
          PowerupFrame();
        }
      } catch (Exception e) {
        Error(e, "OnTick(" + state + ")");
      }
    }
    static DateTime NextTickFrame;
    void Loaded() {
      HasConfig = true;
      Plugin = this;
      Players = new Dictionary<string, PlayerData>();
    }
    void Unload() {
      InvUnload();
      LocaleUnload();
      ApiUnload();
      Players.Clear();
    }
    void IOnServerShutdown() { Notify("========== SHUTDOWN =========="); }
  }
}