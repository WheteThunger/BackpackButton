using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Oxide.Core;
using Oxide.Core.Libraries.Covalence;
using Oxide.Core.Plugins;
using Oxide.Game.Rust.Cui;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Network;
using Newtonsoft.Json.Converters;
using UnityEngine;
using UnityEngine.UI;
using Time = UnityEngine.Time;

namespace Oxide.Plugins
{
    [Info("Backpack Button", "WhiteThunder", "1.1.2")]
    [Description("Adds a button which allows players to open their backpack, with multiple advanced features.")]
    internal class BackpackButton : CovalencePlugin
    {
        #region Fields

        private const string UsagePermission = "backpackbutton.use";

        private const int SaddleBagItemId = 1400460850;

        private Configuration _config;
        private SavedData _data;
        private BackpacksApi _backpacksApi;
        private readonly HashSet<ulong> _uiViewers = new HashSet<ulong>();
        private readonly UiUpdateManager _uiUpdateManager;

        [PluginReference]
        private readonly Plugin Backpacks;

        private static readonly VersionNumber RequiredBackpacksVersion = new VersionNumber(3, 11, 0);

        public BackpackButton()
        {
            _uiUpdateManager = new UiUpdateManager(this);
        }

        #endregion

        #region Hooks

        private void Init()
        {
            _config.Init();
            _data = SavedData.Load();

            permission.RegisterPermission(UsagePermission, this);

            AddCovalenceCommand(_config.Commands, nameof(BackpackButtonCommand));

            Unsubscribe(nameof(OnPlayerSleep));
            Unsubscribe(nameof(OnPlayerSleepEnded));
        }

        private void OnServerInitialized()
        {
            if (Backpacks == null)
            {
                LogError($"Backpacks is not loaded. Get it at https://umod.org.");
            }

            HandleBackpacksLoaded();

            timer.Every(900, _data.SaveIfChanged);

            Subscribe(nameof(OnPlayerSleep));
            Subscribe(nameof(OnPlayerSleepEnded));
        }

        private void Unload()
        {
            _uiUpdateManager.Unload();

            foreach (var player in BasePlayer.activePlayerList)
            {
                DestroyUiIfActive(player);
            }

            _data.SaveIfChanged();
        }

        private void OnPluginLoaded(Plugin plugin)
        {
            if (plugin.Name == nameof(Backpacks))
            {
                HandleBackpacksLoaded();
            }
        }

        private void OnPluginUnloaded(Plugin plugin)
        {
            if (plugin.Name == nameof(Backpacks))
            {
                _backpacksApi = null;

                foreach (var player in BasePlayer.activePlayerList)
                {
                    DestroyUiIfActive(player);
                }
            }
        }

        private void OnGroupPermissionGranted(string groupName, string perm)
        {
            if (!perm.Equals(UsagePermission))
                return;

            foreach (var player in BasePlayer.activePlayerList)
            {
                if (!permission.UserHasGroup(player.UserIDString, groupName))
                    continue;

                HandlePermissionChanged(player);
            }
        }

        private void OnGroupPermissionRevoked(string groupName, string perm)
        {
            OnGroupPermissionGranted(groupName, perm);
        }

        private void OnUserPermissionGranted(string userId, string perm)
        {
            if (!perm.Equals(UsagePermission))
                return;

            var player = BasePlayer.Find(userId);
            if (player != null)
            {
                HandlePermissionChanged(player);
            }
        }

        private void OnUserPermissionRevoked(string userId, string perm)
        {
            OnUserPermissionGranted(userId, perm);
        }

        private void OnPlayerConnected(BasePlayer player)
        {
            CreateUiIfEnabled(player);
        }

        private void OnPlayerRespawned(BasePlayer player)
        {
            CreateUiIfEnabled(player);
        }

        private void OnPlayerSleepEnded(BasePlayer player)
        {
            CreateUiIfEnabled(player);
        }

        private void OnPlayerSleep(BasePlayer player)
        {
            DestroyUiIfActive(player);
        }

        // Handle player death while sleeping in a safe zone.
        private void OnEntityKill(BasePlayer player)
        {
            if (player.IsNpc)
                return;

            DestroyUiIfActive(player);
        }

        // Handle player death by normal means.
        private void OnEntityDeath(BasePlayer player, HitInfo info) => OnEntityKill(player);

        private void OnEntityMounted(ComputerStation station, BasePlayer player) => DestroyUiIfActive(player);

        private void OnEntityDismounted(ComputerStation station, BasePlayer player) => CreateUiIfEnabled(player);

        private void OnNpcConversationStart(NPCTalking npcTalking, BasePlayer player, ConversationData conversationData)
        {
            // This delay can be removed in the future if an OnNpcConversationStarted hook is created.
            NextTick(() =>
            {
                // Verify the conversation started, since another plugin may have blocked it.
                if (!npcTalking.conversingPlayers.Contains(player))
                    return;

                DestroyUiIfActive(player);
            });
        }

        private void OnNpcConversationEnded(NPCTalking npcTalking, BasePlayer player) => CreateUiIfEnabled(player);

        #endregion

        #region Dependencies

        private class BackpacksApi
        {
            public static BackpacksApi Parse(Dictionary<string, object> dict)
            {
                var backpacksApi = new BackpacksApi();

                GetOption(dict, "IsBackpackLoaded", out backpacksApi.IsBackpackLoaded);
                GetOption(dict, "GetBackpackCapacity", out backpacksApi.GetBackpackCapacity);
                GetOption(dict, "IsBackpackGathering", out backpacksApi.IsBackpackGathering);
                GetOption(dict, "IsBackpackRetrieving", out backpacksApi.IsBackpackRetrieving);
                GetOption(dict, "CountBackpackItems", out backpacksApi.CountBackpackItems);

                return backpacksApi;
            }

            public Func<BasePlayer, bool> IsBackpackLoaded;
            public Func<BasePlayer, int> GetBackpackCapacity;
            public Func<BasePlayer, bool> IsBackpackGathering;
            public Func<BasePlayer, bool> IsBackpackRetrieving;
            public Func<ulong, Dictionary<string, object>, int> CountBackpackItems;

            private static void GetOption<T>(Dictionary<string, object> dict, string key, out T result)
            {
                object value;
                result = dict.TryGetValue(key, out value) && value is T
                    ? (T)value
                    : default(T);
            }
        }

        #endregion

        #region Commands

        private void BackpackButtonCommand(IPlayer player, string command, string[] args)
        {
            BasePlayer basePlayer;
            if (!VerifyPlayer(player, out basePlayer)
                || !VerifyHasPermission(player, UsagePermission)
                || _config.NumValidPositions == 0)
                return;

            var positionNameArg = args.FirstOrDefault();
            if (string.IsNullOrWhiteSpace(positionNameArg) || IsKeyBindArg(positionNameArg))
            {
                PrintCommandUsage(player, basePlayer, command);
                return;
            }

            var localizedToggleOption = GetMessage(player.Id, "Toggle");
            if (StringUtils.EqualsIgnoreCase(positionNameArg, localizedToggleOption))
            {
                var enabled = _data.GetEnabledPreference(basePlayer.userID) ?? _config.DefaultButtonPosition != null;
                _data.SetEnabledPreference(basePlayer.userID, !enabled);

                DestroyUiIfActive(basePlayer);
                CreateUiIfEnabled(basePlayer);
                return;
            }

            var localizedOffOption = GetMessage(player.Id, "Off");
            if (StringUtils.EqualsIgnoreCase(positionNameArg, localizedOffOption))
            {
                DestroyUiIfActive(basePlayer);

                if (_config.DefaultButtonPosition == null)
                {
                    _data.RemoveEnabledPreference(basePlayer.userID);
                }
                else
                {
                    _data.SetEnabledPreference(basePlayer.userID, false);
                }

                _data.RemovePositionPreference(basePlayer.userID);
                return;
            }

            var buttonPosition = GetMatchingButtonPosition(basePlayer, positionNameArg);
            if (buttonPosition == null)
            {
                PrintCommandUsage(player, basePlayer, command);
                return;
            }

            var defaultEnabled = _config.DefaultButtonPosition != null;
            var defaultPositionName = _config.DefaultButtonPosition?.Name;

            if (defaultEnabled)
            {
                _data.RemoveEnabledPreference(basePlayer.userID);
            }
            else
            {
                _data.SetEnabledPreference(basePlayer.userID, true);
            }

            if (defaultEnabled && StringUtils.EqualsIgnoreCase(buttonPosition.Name, defaultPositionName))
            {
                _data.RemovePositionPreference(basePlayer.userID);
            }
            else
            {
                _data.SetPositionPreference(basePlayer.userID, buttonPosition.Name);
            }

            CreateUiIfEnabled(basePlayer);
        }

        #endregion

        #region Helpers

        private static class StringUtils
        {
            public static bool EqualsIgnoreCase(string a, string b) =>
                string.Compare(a, b, StringComparison.OrdinalIgnoreCase) == 0;
        }

        private static void LogDebug(string message) => Interface.Oxide.LogDebug($"[Backpack Button] {message}");
        private static void LogInfo(string message) => Interface.Oxide.LogInfo($"[Backpack Button] {message}");
        private static void LogWarning(string message) => Interface.Oxide.LogWarning($"[Backpack Button] {message}");
        private static void LogError(string message) => Interface.Oxide.LogError($"[Backpack Button] {message}");

        private static bool IsKeyBindArg(string arg)
        {
            return arg == "True";
        }

        private static bool VerifyPlayer(IPlayer player, out BasePlayer basePlayer)
        {
            if (player.IsServer)
            {
                basePlayer = null;
                return false;
            }

            basePlayer = player.Object as BasePlayer;
            return true;
        }

        private bool VerifyBackpacksVersion()
        {
            if (Backpacks == null)
                return false;

            if (Backpacks.Version >= RequiredBackpacksVersion)
                return true;

            LogError($"Backpacks v{Backpacks.Version} is loaded, but this plugin requires Backpacks v{RequiredBackpacksVersion}+");
            return false;
        }

        private bool VerifyHasPermission(IPlayer player, string perm)
        {
            if (player.HasPermission(perm))
                return true;

            player.Reply(GetMessage(player, "No Permission"));
            return false;
        }

        private ButtonPosition GetPlayerButtonPosition(BasePlayer player)
        {
            var enabled = _data.GetEnabledPreference(player.userID) ?? _config.DefaultButtonPosition != null;
            if (!enabled)
                return null;

            var positionName = _data.GetPositionPreference(player.userID) ?? _config.DefaultButtonPosition?.Name;
            if (positionName == null)
                return null;

            return _config.GetButtonPosition(positionName);
        }

        private void PrintCommandUsage(IPlayer player, BasePlayer basePlayer, string command)
        {
            var currentButtonPosition = GetPlayerButtonPosition(basePlayer);
            var localizedButtonNameList = new List<string>();

            foreach (var buttonPosition in _config.ButtonPositions)
            {
                if (!buttonPosition.Enabled || buttonPosition.LangKey == null)
                    continue;

                var localizedButtonName = GetMessage(player.Id, buttonPosition.LangKey);
                localizedButtonNameList.Add(buttonPosition == currentButtonPosition
                    ? $"<color=#5bf>{localizedButtonName}</color>"
                    : localizedButtonName);
            }

            localizedButtonNameList.Add(GetMessage(player.Id, "Toggle"));

            var localizedOffName = GetMessage(player.Id, "Off");
            localizedButtonNameList.Add(currentButtonPosition == null
                ? $"<color=#5bf>{localizedOffName}</color>"
                : localizedOffName);

            if (player.LastCommand == CommandType.Chat)
            {
                command = $"/{command}";
            }

            var localizedUsageTemplate = GetMessage(player.Id, "Usage");
            player.Reply(string.Format(localizedUsageTemplate, command, string.Join(" | ", localizedButtonNameList)));
        }

        private void HandleBackpacksLoaded()
        {
            if (!VerifyBackpacksVersion())
                return;

            var apiDict = Backpacks.Call("API_GetApi") as Dictionary<string, object>;
            _backpacksApi = BackpacksApi.Parse(apiDict);
            if (_backpacksApi == null)
            {
                LogError("Failed to integrate with Backpacks Api.");
                return;
            }

            var hooks = new Dictionary<string, object>
            {
                ["OnBackpackLoaded"] = new Action<BasePlayer, int, int>((player, occupiedSlots, totalSlots) =>
                {
                    if (_uiViewers.Contains(player.userID))
                    {
                        ButtonUi.UpdateUi(this, player);
                    }
                    else
                    {
                        CreateUiIfEnabled(player);
                    }
                })
            };

            if (_config.FillBar.Enabled || _config.Slots.Enabled)
            {
                hooks["OnBackpackItemCountChanged"] = new Action<BasePlayer, int, int>((player, occupiedSlots, totalSlots) =>
                {
                    if (_uiViewers.Contains(player.userID))
                    {
                        _uiUpdateManager.ScheduleUpdate(player);
                    }
                });
            }

            if (_config.GatherMode.Enabled)
            {
                hooks["OnBackpackGatherChanged"] = new Action<BasePlayer, bool>((player, enabled) =>
                {
                    if (_uiViewers.Contains(player.userID))
                    {
                        _uiUpdateManager.ScheduleUpdate(player);
                    }
                });
            }

            if (_config.RetrieveMode.Enabled)
            {
                hooks["OnBackpackRetrieveChanged"] = new Action<BasePlayer, bool>((player, enabled) =>
                {
                    if (_uiViewers.Contains(player.userID))
                    {
                        _uiUpdateManager.ScheduleUpdate(player);
                    }
                });
            }

            Backpacks.Call("API_AddSubscriber", this, hooks);

            foreach (var player in BasePlayer.activePlayerList)
            {
                CreateUiIfEnabled(player);
            }
        }

        private bool ShouldDisplayUi(BasePlayer player, out ButtonPosition buttonPosition)
        {
            buttonPosition = null;

            if (_backpacksApi == null)
                return false;

            if (player == null || player.IsNpc || !player.IsAlive() || player.IsSleeping())
                return false;

            if (!permission.UserHasPermission(player.UserIDString, UsagePermission))
                return false;

            var enabled = _data.GetEnabledPreference(player.userID) ?? _config.DefaultButtonPosition != null;
            if (!enabled)
                return false;

            buttonPosition = GetPlayerButtonPosition(player);
            return buttonPosition != null;
        }

        private bool ShouldDisplayUi(BasePlayer player)
        {
            ButtonPosition buttonPosition;
            return ShouldDisplayUi(player, out buttonPosition);
        }

        private void CreateUiIfEnabled(BasePlayer player)
        {
            ButtonPosition buttonPosition;
            if (!ShouldDisplayUi(player, out buttonPosition))
                return;

            _uiViewers.Add(player.userID);
            ButtonUi.CreateUi(this, player, buttonPosition);
        }

        private void DestroyUiIfActive(BasePlayer player)
        {
            if (!_uiViewers.Remove(player.userID))
                return;

            ButtonUi.DestroyUi(player);
        }

        private void HandlePermissionChanged(BasePlayer player)
        {
            if (ShouldDisplayUi(player))
            {
                CreateUiIfEnabled(player);
            }
            else
            {
                DestroyUiIfActive(player);
            }
        }

        private ButtonPosition GetMatchingButtonPosition(BasePlayer player, string positionName)
        {
            foreach (var buttonPosition in _config.ButtonPositions)
            {
                if (!buttonPosition.Enabled || buttonPosition.LangKey == null)
                    continue;

                var localizedPositionName = GetMessage(player.UserIDString, buttonPosition.LangKey);
                if (StringUtils.EqualsIgnoreCase(positionName, localizedPositionName))
                    return buttonPosition;
            }

            return null;
        }

        #endregion

        #region UI Update Manager

        private class UiUpdateManager
        {
            private const float UpdateDelaySeconds = 0.5f;
            private const float ProcessFrequencySeconds = 0.1f;

            private BackpackButton _plugin;
            private Action _updateAction;
            private Dictionary<BasePlayer, float> _scheduledUpdates = new Dictionary<BasePlayer, float>();
            private List<BasePlayer> _playersProcessed = new List<BasePlayer>();

            public UiUpdateManager(BackpackButton plugin)
            {
                _plugin = plugin;
                _updateAction = ProcessUpdates;
            }

            public void ScheduleUpdate(BasePlayer player)
            {
                var currentlyRunning = _scheduledUpdates.Count > 0;
                _scheduledUpdates[player] = Time.time + UpdateDelaySeconds;

                if (!currentlyRunning)
                {
                    ServerMgr.Instance.InvokeRepeating(_updateAction, ProcessFrequencySeconds, ProcessFrequencySeconds);
                }
            }

            public void Unload()
            {
                ServerMgr.Instance.CancelInvoke(_updateAction);
                _scheduledUpdates.Clear();
            }

            private void ProcessUpdates()
            {
                _playersProcessed.Clear();

                var now = Time.time;

                foreach (var entry in _scheduledUpdates)
                {
                    if (entry.Value > now)
                        continue;

                    var player = entry.Key;
                    _playersProcessed.Add(player);

                    if (_plugin._uiViewers.Contains(player.userID))
                    {
                        ButtonUi.UpdateUi(_plugin, player);
                    }
                }

                foreach (var player in _playersProcessed)
                {
                    _scheduledUpdates.Remove(player);
                }

                _playersProcessed.Clear();

                if (_scheduledUpdates.Count == 0)
                {
                    ServerMgr.Instance.CancelInvoke(_updateAction);
                }
            }
        }

        #endregion

        #region String Cache

        private interface IStringCache
        {
            string Get<T>(T value);
            string Get<T>(T value, Func<T, string> createString);
            string Get(bool value);
        }

        private sealed class DefaultStringCache : IStringCache
        {
            public static readonly DefaultStringCache Instance = new DefaultStringCache();

            private static class StaticStringCache<T>
            {
                private static readonly Dictionary<T, string> _cacheByValue = new Dictionary<T, string>();

                public static string Get(T value)
                {
                    string str;
                    if (!_cacheByValue.TryGetValue(value, out str))
                    {
                        str = value.ToString();
                        _cacheByValue[value] = str;
                    }

                    return str;
                }
            }

            private static class StaticStringCacheWithFactory<T>
            {
                private static readonly Dictionary<Func<T, string>, Dictionary<T, string>> _cacheByDelegate =
                    new Dictionary<Func<T, string>, Dictionary<T, string>>();

                public static string Get(T value, Func<T, string> createString)
                {
                    if (createString.Target != null)
                        throw new InvalidOperationException($"{typeof(StaticStringCacheWithFactory<T>).Name} only accepts open delegates");

                    Dictionary<T, string> cache;
                    if (!_cacheByDelegate.TryGetValue(createString, out cache))
                    {
                        cache = new Dictionary<T, string>();
                        _cacheByDelegate[createString] = cache;
                    }

                    string str;
                    if (!cache.TryGetValue(value, out str))
                    {
                        str = createString(value);
                        cache[value] = str;
                    }

                    return str;
                }
            }

            private DefaultStringCache() {}

            public string Get<T>(T value)
            {
                return StaticStringCache<T>.Get(value);
            }

            public string Get(bool value)
            {
                return value ? "true" : "false";
            }

            public string Get<T>(T value, Func<T, string> createString)
            {
                return StaticStringCacheWithFactory<T>.Get(value, createString);
            }
        }

        #endregion

        #region UI Builder

        private interface IUiSerializable
        {
            void Serialize(IUiBuilder uiBuilder);
        }

        private interface IUiBuilder
        {
            IStringCache StringCache { get; }
            void Start();
            void End();
            void StartElement();
            void EndElement();
            void StartComponent();
            void EndComponent();
            void AddField<T>(string key, T value);
            void AddField(string key, string value);
            void AddXY(string key, float x, float y);
            void AddSerializable<T>(T serializable) where T : IUiSerializable;
            void AddComponents<T>(T components) where T : IUiComponentCollection;
            string ToJson();
            byte[] GetBytes();
            void AddUi(SendInfo sendInfo);
            void AddUi(BasePlayer player);
        }

        private class UiBuilder : IUiBuilder
        {
            private static NetWrite ClientRPCStart(BaseEntity entity, string funcName)
            {
                if (Net.sv.IsConnected() && entity.net != null)
                {
                    var write = Net.sv.StartWrite();
                    write.PacketID(Message.Type.RPCMessage);
                    write.EntityID(entity.net.ID);
                    write.UInt32(StringPool.Get(funcName));
                    return write;
                }
                return null;
            }

            public static readonly UiBuilder Default = new UiBuilder(65536);

            private enum State
            {
                Empty,
                ElementList,
                Element,
                ComponentList,
                Component,
                Complete
            }

            public int Length { get; private set; }

            private const char Delimiter = ',';
            private const char Quote = '"';
            private const char Colon = ':';
            private const char Space = ' ';
            private const char OpenBracket = '[';
            private const char CloseBracket = ']';
            private const char OpenCurlyBrace = '{';
            private const char CloseCurlyBrace = '}';

            private const int MinCapacity = 1024;
            private const int DefaultCapacity = 4096;

            public IStringCache StringCache { get; }
            private char[] _chars;
            private byte[] _bytes;
            private State _state;
            private bool _needsDelimiter;

            public UiBuilder(int capacity, IStringCache stringCache)
            {
                if (capacity < MinCapacity)
                    throw new InvalidOperationException($"Capacity must be at least {MinCapacity}");

                Resize(capacity);
                StringCache = stringCache;
            }

            public UiBuilder(int capacity = DefaultCapacity) : this(capacity, DefaultStringCache.Instance) {}

            public void Start()
            {
                Reset();
                StartArray();
                _state = State.ElementList;
            }

            public void End()
            {
                ValidateState(State.ElementList);
                EndArray();
                _state = State.Complete;
            }

            public void StartElement()
            {
                ValidateState(State.ElementList);
                StartObject();
                _state = State.Element;
            }

            public void EndElement()
            {
                ValidateState(State.Element);
                EndObject();
                _state = State.ElementList;
            }

            public void StartComponent()
            {
                ValidateState(State.ComponentList);
                StartObject();
                _state = State.Component;
            }

            public void EndComponent()
            {
                ValidateState(State.Component);
                EndObject();
                _state = State.ComponentList;
            }

            public void AddField<T>(string key, T value)
            {
                AddKey(key);
                Append(StringCache.Get(value));
                _needsDelimiter = true;
            }

            public void AddField(string key, string value)
            {
                if (value == null)
                    return;

                AddKey(key);
                Append(Quote);
                Append(value);
                Append(Quote);
                _needsDelimiter = true;
            }

            public void AddXY(string key, float x, float y)
            {
                AddKey(key);
                Append(Quote);
                Append(StringCache.Get(x));
                Append(Space);
                Append(StringCache.Get(y));
                Append(Quote);
                _needsDelimiter = true;
            }

            public void AddSerializable<T>(T serializable) where T : IUiSerializable
            {
                serializable.Serialize(this);
            }

            public void AddComponents<T>(T components) where T : IUiComponentCollection
            {
                ValidateState(State.Element);
                AddKey("components");
                StartArray();
                _state = State.ComponentList;
                components.Serialize(this);
                EndArray();
                _state = State.Element;
            }

            public string ToJson()
            {
                ValidateState(State.Complete);
                return new string(_chars, 0, Length);
            }

            public byte[] GetBytes()
            {
                ValidateState(State.Complete);
                var bytes = new byte[Length];
                Buffer.BlockCopy(_bytes, 0, bytes, 0, Length);
                return bytes;
            }

            public void AddUi(SendInfo sendInfo)
            {
                var write = ClientRPCStart(CommunityEntity.ServerInstance, "AddUI");
                if (write != null)
                {
                    var byteCount = Encoding.UTF8.GetBytes(_chars, 0, Length, _bytes, 0);
                    write.BytesWithSize(_bytes, byteCount);
                    write.Send(sendInfo);
                }
            }

            public void AddUi(BasePlayer player)
            {
                AddUi(new SendInfo(player.Connection));
            }

            private void ValidateState(State desiredState)
            {
                if (_state != desiredState)
                    throw new InvalidOperationException($"Expected state {desiredState} but found {_state}");
            }

            private void ValidateState(State desiredState, State alternateState)
            {
                if (_state != desiredState && _state != alternateState)
                    throw new InvalidOperationException($"Expected state {desiredState} or {alternateState} but found {_state}");
            }

            private void Resize(int length)
            {
                Array.Resize(ref _chars, length);
                Array.Resize(ref _bytes, length * 2);
            }

            private void ResizeIfApproachingLength()
            {
                if (Length + 1024 > _chars.Length)
                {
                    Resize(_chars.Length * 2);
                }
            }

            private void Append(char @char)
            {
                _chars[Length++] = @char;
            }

            private void Append(string str)
            {
                for (var i = 0; i < str.Length; i++)
                {
                    _chars[Length + i] = str[i];
                }

                Length += str.Length;
            }

            private void AddDelimiter()
            {
                Append(Delimiter);
            }

            private void AddDelimiterIfNeeded()
            {
                if (_needsDelimiter)
                {
                    AddDelimiter();
                }
            }

            private void StartObject()
            {
                AddDelimiterIfNeeded();
                Append(OpenCurlyBrace);
                _needsDelimiter = false;
            }

            private void EndObject()
            {
                Append(CloseCurlyBrace);
                _needsDelimiter = true;
            }

            private void StartArray()
            {
                Append(OpenBracket);
                _needsDelimiter = false;
            }

            private void EndArray()
            {
                Append(CloseBracket);
                _needsDelimiter = true;
            }

            private void AddKey(string key)
            {
                ValidateState(State.Element, State.Component);
                ResizeIfApproachingLength();
                AddDelimiterIfNeeded();
                Append(Quote);
                Append(key);
                Append(Quote);
                Append(Colon);
            }

            private void Reset()
            {
                Length = 0;
                _state = State.Empty;
                _needsDelimiter = false;
            }
        }

        #endregion

        #region UI Layout

        private struct UiRect
        {
            public string Anchor;
            public float XMin;
            public float XMax;
            public float YMin;
            public float YMax;
        }

        private static class Layout
        {
            [Flags]
            public enum Option
            {
                AnchorBottom = 1 << 0,
                AnchorRight = 1 << 1,
                Vertical = 1 << 2
            }

            public const string AnchorBottomLeft = "0 0";
            public const string AnchorBottomRight = "1 0";
            public const string AnchorTopLeft = "0 1";
            public const string AnchorTopRight = "1 1";

            public const string AnchorBottomCenter = "0.5 0";
            public const string AnchorTopCenter = "0.5 1";
            public const string AnchorCenterLeft = "0 0.5";
            public const string AnchorCenterRight = "1 0.5";

            public static string DetermineAnchor(Option options)
            {
                return options.HasFlag(Option.AnchorBottom)
                    ? options.HasFlag(Option.AnchorRight) ? AnchorBottomRight : AnchorBottomLeft
                    : options.HasFlag(Option.AnchorRight) ? AnchorTopRight : AnchorTopLeft;
            }
        }

        private interface ILayoutProvider {}

        private struct StatelessLayoutProvider : ILayoutProvider
        {
            public static UiRect GetRect(int index, Layout.Option options, Vector2 size, float spacing = 0, Vector2 offset = default(Vector2))
            {
                var xMin = !options.HasFlag(Layout.Option.Vertical)
                    ? offset.x + index * (spacing + size.x)
                    : offset.x;

                var xMax = xMin + size.x;

                var yMin = options.HasFlag(Layout.Option.Vertical)
                    ? offset.y + index * (spacing + size.y)
                    : offset.y;

                var yMax = yMin + size.y;

                if (options.HasFlag(Layout.Option.AnchorRight))
                {
                    var temp = xMin;
                    xMin = -xMax;
                    xMax = -temp;
                }

                if (!options.HasFlag(Layout.Option.AnchorBottom))
                {
                    var temp = yMin;
                    yMin = -yMax;
                    yMax = -temp;
                }

                return new UiRect
                {
                    Anchor = Layout.DetermineAnchor(options),
                    XMin = xMin,
                    XMax = xMax,
                    YMin = yMin,
                    YMax = yMax,
                };
            }

            public Layout.Option Options;
            public Vector2 Offset;
            public Vector2 Size;
            public float Spacing;

            public UiRect this[int index] => GetRect(index, Options, Size, Spacing, Offset);

            public static StatelessLayoutProvider operator +(StatelessLayoutProvider layoutProvider, Vector2 vector)
            {
                layoutProvider.Offset += vector;
                return layoutProvider;
            }

            public static StatelessLayoutProvider operator -(StatelessLayoutProvider layoutProvider, Vector2 vector)
            {
                layoutProvider.Offset -= vector;
                return layoutProvider;
            }
        }

        #endregion

        #region UI Components

        private interface IUiComponent : IUiSerializable {}

        private struct UiButtonComponent : IUiComponent
        {
            private const string Type = "UnityEngine.UI.Button";

            private const string DefaultCommand = null;
            private const string DefaultClose = null;
            private const string DefaultSprite = "Assets/Content/UI/UI.Background.Tile.psd";
            private const string DefaultMaterial = "Assets/Icons/IconMaterial.mat";
            private const string DefaultColor = "1 1 1 1";
            private const Image.Type DefaultImageType = Image.Type.Simple;
            private const float DefaultFadeIn = 0;

            public string Command;
            public string Close;
            public string Sprite;
            public string Material;
            public string Color;
            public Image.Type ImageType;
            public float FadeIn;

            public void Serialize(IUiBuilder builder)
            {
                if (Sprite == default(string))
                    Sprite = DefaultSprite;

                if (Material == default(string))
                    Material = DefaultMaterial;

                if (Color == default(string))
                    Color = DefaultColor;

                if (ImageType == default(Image.Type))
                    ImageType = DefaultImageType;

                builder.StartComponent();
                builder.AddField("type", Type);

                if (Command != DefaultCommand)
                    builder.AddField("command", Command);

                if (Close != DefaultClose)
                    builder.AddField("close", Close);

                if (Sprite != DefaultSprite)
                    builder.AddField("sprite", Sprite);

                if (Material != DefaultMaterial)
                    builder.AddField("material", Material);

                if (Color != DefaultColor)
                    builder.AddField("color", Color);

                if (ImageType != DefaultImageType)
                    builder.AddField("imagetype", builder.StringCache.Get(ImageType));

                if (FadeIn != DefaultFadeIn)
                    builder.AddField("fadeIn", FadeIn);

                builder.EndComponent();
            }
        }

        private struct UiImageComponent : IUiComponent
        {
            private const string Type = "UnityEngine.UI.Image";

            private const string DefaultSprite = "Assets/Content/UI/UI.Background.Tile.psd";
            private const string DefaultMaterial = "Assets/Icons/IconMaterial.mat";
            private const string DefaultColor = "1 1 1 1";
            private const Image.Type DefaultImageType = Image.Type.Simple;
            private const string DefaultPng = null;
            private const int DefaultItemId = 0;
            private const ulong DefaultSkinId = 0;
            private const float DefaultFadeIn = 0;

            public string Sprite;
            public string Material;
            public string Color;
            public Image.Type ImageType;
            public string Png;
            public int ItemId;
            public ulong SkinId;
            public float FadeIn;

            public void Serialize(IUiBuilder builder)
            {
                if (Sprite == default(string))
                    Sprite = DefaultSprite;

                if (Material == default(string))
                    Material = DefaultMaterial;

                if (Color == default(string))
                    Color = DefaultColor;

                if (ImageType == default(Image.Type))
                    ImageType = DefaultImageType;

                builder.StartComponent();
                builder.AddField("type", Type);

                if (Sprite != DefaultSprite)
                    builder.AddField("sprite", Sprite);

                if (Material != DefaultMaterial)
                    builder.AddField("material", Material);

                if (Color != DefaultColor)
                    builder.AddField("color", Color);

                if (ImageType != DefaultImageType)
                    builder.AddField("imagetype", builder.StringCache.Get(ImageType));

                if (Png != DefaultPng)
                    builder.AddField("png", Png);

                if (ItemId != DefaultItemId)
                    builder.AddField("itemid", ItemId);

                if (SkinId != DefaultSkinId)
                    builder.AddField("skinid", SkinId);

                if (FadeIn != DefaultFadeIn)
                    builder.AddField("fadeIn", FadeIn);

                builder.EndComponent();
            }
        }

        private struct UiRawImageComponent : IUiComponent
        {
            private const string Type = "UnityEngine.UI.RawImage";

            private const string DefaultSprite = "Assets/Icons/rust.png";
            private const string DefaultColor = "1 1 1 1";
            private const string DefaultMaterial = null;
            private const string DefaultUrl = null;
            private const string DefaultPng = null;
            private const float DefaultFadeIn = 0;

            public string Sprite;
            public string Color;
            public string Material;
            public string Url;
            public string Png;
            public float FadeIn;

            public void Serialize(IUiBuilder builder)
            {
                if (Sprite == default(string))
                    Sprite = DefaultSprite;

                if (Color == default(string))
                    Color = DefaultColor;

                builder.StartComponent();
                builder.AddField("type", Type);

                if (Sprite != DefaultSprite)
                    builder.AddField("sprite", Sprite);

                if (Color != DefaultColor)
                    builder.AddField("color", Color);

                if (Material != DefaultMaterial)
                    builder.AddField("material", Material);

                if (Url != DefaultUrl)
                    builder.AddField("url", Url);

                if (Png != DefaultPng)
                    builder.AddField("png", Png);

                if (FadeIn != DefaultFadeIn)
                    builder.AddField("fadeIn", FadeIn);

                builder.EndComponent();
            }
        }

        private struct UiRectTransformComponent : IUiComponent
        {
            private const string Type = "RectTransform";

            public const string DefaultAnchorMin = "0.0 0.0";
            public const string DefaultAnchorMax = "1.0 1.0";
            public const string DefaultOffsetMin = "0.0 0.0";
            public const string DefaultOffsetMax = "1.0 1.0";

            public string AnchorMin;
            public string AnchorMax;
            public string OffsetMin;
            public string OffsetMax;

            public void Serialize(IUiBuilder builder)
            {
                if (AnchorMin == default(string))
                    AnchorMin = DefaultAnchorMin;

                if (AnchorMax == default(string))
                    AnchorMax = DefaultAnchorMax;

                if (OffsetMin == default(string))
                    OffsetMin = DefaultOffsetMin;

                if (OffsetMax == default(string))
                    OffsetMax = DefaultOffsetMax;

                builder.StartComponent();
                builder.AddField("type", Type);

                if (AnchorMin != DefaultAnchorMin)
                    builder.AddField("anchormin", AnchorMin);

                if (AnchorMax != DefaultAnchorMax)
                    builder.AddField("anchormax", AnchorMax);

                if (OffsetMin != DefaultOffsetMin)
                    builder.AddField("offsetmin", OffsetMin);

                if (OffsetMax != DefaultOffsetMax)
                    builder.AddField("offsetmax", OffsetMax);

                builder.EndComponent();
            }
        }

        private struct UiTextComponent : IUiComponent
        {
            private const string Type = "UnityEngine.UI.Text";

            private const string DefaultText = "Text";
            private const int DefaultFontSize = 14;
            private const string DefaultFont = "RobotoCondensed-Bold.ttf";
            private const TextAnchor DefaultTextAlign = TextAnchor.UpperLeft;
            private const string DefaultColor = "1 1 1 1";
            private const VerticalWrapMode DefaultVerticalWrapMode = VerticalWrapMode.Truncate;
            private const float DefaultFadeIn = 0;

            public string Text;
            public int FontSize;
            public string Font;
            public TextAnchor TextAlign;
            public string Color;
            public VerticalWrapMode VerticalWrapMode;
            public float FadeIn;

            public void Serialize(IUiBuilder builder)
            {
                if (Text == default(string))
                    Text = DefaultText;

                if (FontSize == default(int))
                    FontSize = DefaultFontSize;

                if (Font == default(string))
                    Font = DefaultFont;

                if (TextAlign == default(TextAnchor))
                    TextAlign = DefaultTextAlign;

                if (Color == default(string))
                    Color = DefaultColor;

                if (VerticalWrapMode == default(VerticalWrapMode))
                    VerticalWrapMode = DefaultVerticalWrapMode;

                builder.StartComponent();
                builder.AddField("type", Type);

                if (Text != DefaultText)
                    builder.AddField("text", Text);

                if (FontSize != DefaultFontSize)
                    builder.AddField("fontSize", FontSize);

                if (Font != DefaultFont)
                    builder.AddField("font", Font);

                if (TextAlign != DefaultTextAlign)
                    builder.AddField("align", builder.StringCache.Get(TextAlign));

                if (Color != DefaultColor)
                    builder.AddField("color", Color);

                if (VerticalWrapMode != DefaultVerticalWrapMode)
                    builder.AddField("verticalOverflow", builder.StringCache.Get(VerticalWrapMode));

                if (FadeIn != DefaultFadeIn)
                    builder.AddField("fadeIn", FadeIn);

                builder.EndComponent();
            }
        }

        private struct UiOutlineComponent : IUiComponent
        {
            private const string Type = "UnityEngine.UI.Outline";

            private const string DefaultColor = "1 1 1 1";
            private const string DefaultDistance = "1.0 -1.0";
            private const bool DefaultUseGraphicAlpha = false;

            public string Color;
            public string Distance;
            public bool UseGraphicAlpha;

            public void Serialize(IUiBuilder builder)
            {
                if (Color == default(string))
                    Color = DefaultColor;

                if (Distance == default(string))
                    Distance = DefaultDistance;

                builder.StartComponent();
                builder.AddField("type", Type);

                if (Color != DefaultColor)
                    builder.AddField("color", Color);

                if (Distance != DefaultDistance)
                    builder.AddField("distance", Distance);

                if (UseGraphicAlpha != DefaultUseGraphicAlpha)
                    builder.AddField("useGraphicAlpha", UseGraphicAlpha);

                builder.EndComponent();
            }
        }

        // Custom component for handling positions.
        private struct UiRectComponent : IUiComponent
        {
            private const string Type = "RectTransform";

            public const string DefaultAnchorMin = "0.0 0.0";

            private const string DefaultAnchor = "0 0";

            public UiRect Rect;

            public UiRectComponent(UiRect rect)
            {
                Rect = rect;
            }

            public UiRectComponent(float x, float y, string anchor = DefaultAnchor)
            {
                Rect = new UiRect
                {
                    Anchor = anchor,
                    XMin = x,
                    XMax = x,
                    YMin = y,
                    YMax = y
                };
            }

            public void Serialize(IUiBuilder builder)
            {
                builder.StartComponent();
                builder.AddField("type", Type);

                if (Rect.Anchor != DefaultAnchorMin)
                {
                    builder.AddField("anchormin", Rect.Anchor);
                    builder.AddField("anchormax", Rect.Anchor);
                }

                builder.AddXY("offsetmin", Rect.XMin, Rect.YMin);
                builder.AddXY("offsetmax", Rect.XMax, Rect.YMax);

                builder.EndComponent();
            }
        }

        #endregion

        #region UI Elements

        private interface IUiComponentCollection : IUiSerializable {}

        private struct UiComponents<T1> : IUiComponentCollection, IEnumerable<IUiComponentCollection>
            where T1 : IUiComponent
        {
            public T1 Component1;

            public void Add(T1 item) => Component1 = item;

            public void Serialize(IUiBuilder builder)
            {
                Component1.Serialize(builder);
            }

            public IEnumerator<IUiComponentCollection> GetEnumerator()
            {
                throw new NotImplementedException();
            }

            IEnumerator IEnumerable.GetEnumerator()
            {
                throw new NotImplementedException();
            }
        }

        private struct UiComponents<T1, T2> : IUiComponentCollection, IEnumerable<IUiComponentCollection>
            where T1 : IUiComponent
            where T2 : IUiComponent
        {
            public T1 Component1;
            public T2 Component2;

            public void Add(T1 item) => Component1 = item;
            public void Add(T2 item) => Component2 = item;

            public void Serialize(IUiBuilder builder)
            {
                Component1.Serialize(builder);
                Component2.Serialize(builder);
            }

            public IEnumerator<IUiComponentCollection> GetEnumerator()
            {
                throw new NotImplementedException();
            }

            IEnumerator IEnumerable.GetEnumerator()
            {
                throw new NotImplementedException();
            }
        }

        private struct UiComponents<T1, T2, T3> : IUiComponentCollection, IEnumerable<IUiComponentCollection>
            where T1 : IUiComponent
            where T2 : IUiComponent
            where T3 : IUiComponent
        {
            public T1 Component1;
            public T2 Component2;
            public T3 Component3;

            public void Add(T1 item) => Component1 = item;
            public void Add(T2 item) => Component2 = item;
            public void Add(T3 item) => Component3 = item;

            public void Serialize(IUiBuilder builder)
            {
                Component1.Serialize(builder);
                Component2.Serialize(builder);
                Component3.Serialize(builder);
            }

            public IEnumerator<IUiComponentCollection> GetEnumerator()
            {
                throw new NotImplementedException();
            }

            IEnumerator IEnumerable.GetEnumerator()
            {
                throw new NotImplementedException();
            }
        }

        private struct UiElement<T> : IUiSerializable
            where T : IUiComponentCollection
        {
            public string Name;
            public string Parent;
            public string DestroyName;
            public float FadeOut;
            public T Components;

            public void Serialize(IUiBuilder builder)
            {
                builder.StartElement();
                builder.AddField("name", Name);
                builder.AddField("parent", Parent);

                if (DestroyName != default(string))
                    builder.AddField("destroyUi", DestroyName);

                if (FadeOut != default(float))
                    builder.AddField("fadeOut", FadeOut);

                builder.AddComponents(Components);
                builder.EndElement();
            }
        }

        #endregion

        #region UI

        private static class ButtonUi
        {
            private const string Name = nameof(BackpackButton);
            private static readonly string ButtonName = $"{Name}.Button";

            private const float YMin = 18;
            private const float ButtonSize = 60;

            public static void CreateUi(BackpackButton plugin, BasePlayer player, ButtonPosition buttonPosition)
            {
                var builder = UiBuilder.Default;
                builder.Start();

                var uiRectComponent = new UiRectComponent(new UiRect
                {
                    Anchor = Layout.AnchorBottomCenter,
                    XMin = buttonPosition.OffsetX,
                    XMax = buttonPosition.OffsetX + ButtonSize,
                    YMin = YMin,
                    YMax = YMin + ButtonSize,
                });

                var config = plugin._config;
                if (config.Background.Enabled)
                {
                    var rawImageComponent = new UiRawImageComponent
                    {
                        Color = config.Background.Color,
                        Sprite = config.Background.Sprite,
                    };

                    builder.AddSerializable(new UiElement<UiComponents<UiRawImageComponent, UiRectComponent>>
                    {
                        Name = Name,
                        DestroyName = Name,
                        Parent = "Hud.Menu",
                        Components = { rawImageComponent, uiRectComponent },
                    });
                }
                else
                {
                    builder.AddSerializable(new UiElement<UiComponents<UiRectComponent>>
                    {
                        Name = Name,
                        DestroyName = Name,
                        Parent = "Hud.Menu",
                        Components = { uiRectComponent },
                    });
                }

                var imageSize = buttonPosition.ImageSize;
                var imageOffset = (ButtonSize - imageSize) / 2f;

                var rectTransformComponent = new UiRectComponent(new UiRect
                {
                    Anchor = "0 0",
                    XMin = imageOffset,
                    XMax = imageOffset + imageSize,
                    YMin = imageOffset,
                    YMax = imageOffset + imageSize
                });

                if (buttonPosition.SkinId != 0)
                {
                    builder.AddSerializable(new UiElement<UiComponents<UiImageComponent, UiRectComponent>>
                    {
                        Parent = Name,
                        Components =
                        {
                            new UiImageComponent { ItemId = SaddleBagItemId, SkinId = buttonPosition.SkinId },
                            rectTransformComponent
                        }
                    });
                }
                else
                {
                    builder.AddSerializable(new UiElement<UiComponents<UiRawImageComponent, UiRectComponent>>
                    {
                        Parent = Name,
                        Components =
                        {
                            new UiRawImageComponent { Url = buttonPosition.Url, },
                            rectTransformComponent
                        }
                    });
                }

                AddButton(builder, plugin, player);

                builder.End();
                builder.AddUi(player);
            }

            public static void UpdateUi(BackpackButton plugin, BasePlayer player)
            {
                var builder = UiBuilder.Default;
                builder.Start();
                AddButton(builder, plugin, player);
                builder.End();
                builder.AddUi(player);
            }

            public static void DestroyUi(BasePlayer player)
            {
                CuiHelper.DestroyUi(player, Name);
            }

            private static void AddButton(UiBuilder builder, BackpackButton plugin, BasePlayer player)
            {
                builder.AddSerializable(new UiElement<UiComponents<UiButtonComponent, UiRectTransformComponent>>
                {
                    Parent = Name,
                    Name = ButtonName,
                    DestroyName = ButtonName,
                    Components =
                    {
                        new UiButtonComponent { Command = "backpack.open", Color = "0 0 0 0" },
                        new UiRectTransformComponent { AnchorMin = "0 0", AnchorMax = "1 1", }
                    }
                });

                var backpacksApi = plugin._backpacksApi;
                if (!backpacksApi.IsBackpackLoaded(player))
                    return;

                var occupiedSlots = plugin._backpacksApi.CountBackpackItems(player.userID, null);
                var capacity = plugin._backpacksApi.GetBackpackCapacity(player);
                var fullFraction = (float)occupiedSlots / capacity;
                var fullPercent = Mathf.CeilToInt(fullFraction * 100);

                var config = plugin._config;
                if (config.FillBar.Enabled)
                {
                    builder.AddSerializable(new UiElement<UiComponents<UiRawImageComponent, UiRectComponent>>
                    {
                        Parent = ButtonName,
                        Components =
                        {
                            new UiRawImageComponent
                            {
                                Sprite = config.FillBar.Sprite,
                                Color = config.FillBar.GetColor(fullPercent)
                            },
                            new UiRectComponent(new UiRect
                            {
                                Anchor = "0 0",
                                XMax = config.FillBar.Width,
                                YMax = ButtonSize * fullFraction,
                            })
                        }
                    });
                }

                if (config.Slots.Enabled)
                {
                    var text = config.Slots.ShowOccupiedSlots && config.Slots.ShowTotalSlots
                        ? $"{DefaultStringCache.Instance.Get(occupiedSlots)}/{DefaultStringCache.Instance.Get(capacity)}"
                        : config.Slots.ShowOccupiedSlots
                            ? DefaultStringCache.Instance.Get(occupiedSlots)
                            : DefaultStringCache.Instance.Get(capacity);

                    builder.AddSerializable(new UiElement<UiComponents<UiRectTransformComponent, UiTextComponent, UiOutlineComponent>>
                    {
                        Parent = ButtonName,
                        Components =
                        {
                            new UiRectTransformComponent
                            {
                                AnchorMin = "0 0",
                                AnchorMax = "0 0",
                                OffsetMin = config.Slots.OffsetMin,
                                OffsetMax = config.Slots.OffsetMax,
                            },
                            new UiTextComponent
                            {
                                Text = text,
                                FontSize = 14,
                                Color = config.Slots.GetColor(fullPercent),
                                TextAlign = TextAnchor.MiddleCenter,
                                VerticalWrapMode = VerticalWrapMode.Overflow
                            },
                            new UiOutlineComponent
                            {
                                Color = "0 0 0 1",
                                Distance = "0.5 0",
                            },
                        }
                    });
                }

                var arrowSize = new Vector2(ButtonSize / 2, ButtonSize / 2);

                if (config.RetrieveMode.Enabled && backpacksApi.IsBackpackRetrieving(player))
                {
                    builder.AddSerializable(new UiElement<UiComponents<UiRectComponent, UiTextComponent>>
                    {
                        Parent = ButtonName,
                        Components =
                        {
                            new UiRectComponent(StatelessLayoutProvider.GetRect(0, Layout.Option.AnchorRight | Layout.Option.Vertical, arrowSize, offset: new Vector2(3, 0))),
                            new UiTextComponent
                            {
                                Text = "↑",
                                FontSize = 16,
                                TextAlign = TextAnchor.UpperRight,
                                Color = config.RetrieveMode.Color,
                                VerticalWrapMode = VerticalWrapMode.Overflow
                            },
                        }
                    });
                }

                if (config.GatherMode.Enabled && backpacksApi.IsBackpackGathering(player))
                {
                    builder.AddSerializable(new UiElement<UiComponents<UiRectComponent, UiTextComponent>>
                    {
                        Parent = ButtonName,
                        Components =
                        {
                            new UiRectComponent(StatelessLayoutProvider.GetRect(0, Layout.Option.AnchorBottom | Layout.Option.AnchorRight | Layout.Option.Vertical, arrowSize, offset: new Vector2(3, 3))),
                            new UiTextComponent
                            {
                                Text = "↓",
                                FontSize = 16,
                                TextAlign = TextAnchor.LowerRight,
                                Color = config.GatherMode.Color,
                                VerticalWrapMode = VerticalWrapMode.Overflow
                            },
                        }
                    });
                }
            }
        }

        #endregion

        #region Data

        [JsonObject(MemberSerialization.OptIn)]
        private class SavedData
        {
            public static SavedData Load()
            {
                var data = Interface.Oxide.DataFileSystem.ReadObject<SavedData>(nameof(BackpackButton)) ?? new SavedData { _dirty = true };
                data.SaveIfChanged();
                return data;
            }

            [JsonIgnore]
            private bool _dirty;

            [JsonProperty("OverridePositionByPlayer")]
            private Dictionary<ulong, string> _overridePositionByPlayer = new Dictionary<ulong, string>();

            [JsonProperty("OverrideEnabledByPlayer")]
            private Dictionary<ulong, bool> _overrideEnabledByPlayer = new Dictionary<ulong, bool>();

            public bool? GetEnabledPreference(ulong userId)
            {
                bool enabled;
                return _overrideEnabledByPlayer.TryGetValue(userId, out enabled)
                    ? enabled as bool?
                    : null;
            }

            public void SetEnabledPreference(ulong userId, bool enabled)
            {
                bool currentlyEnabled;
                if (_overrideEnabledByPlayer.TryGetValue(userId, out currentlyEnabled)
                    && currentlyEnabled == enabled)
                    return;

                _overrideEnabledByPlayer[userId] = enabled;
                _dirty = true;
            }

            public void RemoveEnabledPreference(ulong userId)
            {
                _dirty |= _overrideEnabledByPlayer.Remove(userId);
            }

            public string GetPositionPreference(ulong userId)
            {
                string positionName;
                return _overridePositionByPlayer.TryGetValue(userId, out positionName)
                    ? positionName
                    : null;
            }

            public void SetPositionPreference(ulong userId, string positionName)
            {
                string currentPosition;
                if (_overridePositionByPlayer.TryGetValue(userId, out currentPosition)
                    && StringUtils.EqualsIgnoreCase(currentPosition, positionName))
                    return;

                _overridePositionByPlayer[userId] = positionName;
                _dirty = true;
            }

            public void RemovePositionPreference(ulong userId)
            {
                _dirty |= _overridePositionByPlayer.Remove(userId);
            }

            public void SaveIfChanged()
            {
                if (!_dirty)
                    return;

                Interface.Oxide.DataFileSystem.WriteObject(nameof(BackpackButton), this);
                _dirty = false;
            }
        }

        #endregion

        #region Configuration

        [JsonObject(MemberSerialization.OptIn)]
        private class ButtonPosition
        {
            [JsonProperty("Name")]
            public string Name;

            [JsonProperty("Enabled")]
            public bool Enabled = true;

            [JsonProperty("Offset X")]
            public float OffsetX;

            [JsonProperty("URL")]
            public string Url;

            [JsonProperty("Skin ID")]
            public ulong SkinId;

            [JsonProperty("Image size")]
            public float ImageSize = 60;

            public string LangKey
            {
                get
                {
                    if (_langKey == null)
                    {
                        _langKey = string.IsNullOrWhiteSpace(Name) ? null : $"Position.{Name}";
                    }

                    return _langKey;
                }
            }

            private string _langKey;
        }

        [JsonObject(MemberSerialization.OptIn)]
        private class BackgroundSettings
        {
            [JsonProperty("Enabled")]
            public bool Enabled = true;

            [JsonProperty("Color")]
            public string Color = "0.969 0.922 0.882 0.035";

            [JsonProperty("Sprite")]
            public string Sprite = "assets/content/ui/ui.background.tiletex.psd";
        }

        [JsonObject(MemberSerialization.OptIn)]
        private abstract class DynamicColorConfig
        {
            [JsonProperty("Default color")]
            protected abstract string DefaultColor { get; set; }

            [JsonProperty("Enable dynamic color")]
            protected abstract bool EnableDynamicColor { get; set; }

            [JsonProperty("Dynamic color by fullness percent", ObjectCreationHandling = ObjectCreationHandling.Replace)]
            protected abstract Dictionary<int, string> ColorByFullPercent { get; set; }

            private List<Tuple<int, string>> _sortedColorByFullPercent = new List<Tuple<int, string>>();

            public void Init()
            {
                foreach (var fullPercent in ColorByFullPercent.Keys.OrderBy(amount => amount))
                {
                    _sortedColorByFullPercent.Add(new Tuple<int, string>(fullPercent, ColorByFullPercent[fullPercent]));
                }
            }

            public string GetColor(int fullPercent)
            {
                if (EnableDynamicColor)
                {
                    for (var i = _sortedColorByFullPercent.Count - 1; i >= 0; i--)
                    {
                        var entry = _sortedColorByFullPercent[i];
                        if (fullPercent >= entry.Item1)
                            return entry.Item2;
                    }
                }

                return DefaultColor;
            }
        }

        [JsonObject(MemberSerialization.OptIn)]
        private class SlotSettings : DynamicColorConfig
        {
            [JsonProperty("Show occupied slots")]
            public bool ShowOccupiedSlots = true;

            [JsonProperty("Show total slots")]
            public bool ShowTotalSlots = true;

            [JsonProperty("Offset min")]
            public string OffsetMin = "0 -18";

            [JsonProperty("Offset max")]
            public string OffsetMax = "60 0";

            [JsonProperty("Text align")]
            [JsonConverter(typeof(StringEnumConverter))]
            public TextAnchor TextAnchor = TextAnchor.MiddleCenter;

            public bool Enabled => ShowOccupiedSlots || ShowTotalSlots;

            protected override string DefaultColor { get; set; } = "0.4 0.8 0.4 1";

            protected override bool EnableDynamicColor { get; set; } = true;

            protected override Dictionary<int, string> ColorByFullPercent { get; set; } = new Dictionary<int, string>
            {
                [70] = "0.8 0.8 0.2 1",
                [80] = "0.8 0.4 0.2 1",
                [90] = "0.8 0.2 0.2 1",
            };
        }

        [JsonObject(MemberSerialization.OptIn)]
        private class FillBarSettings : DynamicColorConfig
        {
            [JsonProperty("Enabled")]
            public bool Enabled = true;

            [JsonProperty("Width")]
            public float Width = 4;

            [JsonProperty("Sprite")]
            public string Sprite = "assets/content/ui/ui.background.tiletex.psd";

            // private string DefaultColor = "0.792 0.459 0.251 1";
            protected override string DefaultColor { get; set; } = "0.4 0.8 0.4 1";

            protected override bool EnableDynamicColor { get; set; } = true;

            protected override Dictionary<int, string> ColorByFullPercent { get; set; } = new Dictionary<int, string>
            {
                [70] = "0.8 0.8 0.2 1",
                [80] = "0.8 0.4 0.2 1",
                [90] = "0.8 0.2 0.2 1",
            };
        }

        [JsonObject(MemberSerialization.OptIn)]
        private class GatherModeSettings
        {
            [JsonProperty("Enabled")]
            public bool Enabled = true;

            [JsonProperty("Color")]
            public string Color = "0.4 0.8 1 1";
        }

        [JsonObject(MemberSerialization.OptIn)]
        private class RetrieveModeSettings
        {
            [JsonProperty("Enabled")]
            public bool Enabled = true;

            [JsonProperty("Color")]
            public string Color = "0.4 0.8 1 1";
        }

        [JsonObject(MemberSerialization.OptIn)]
        private class Configuration : BaseConfiguration
        {
            [JsonProperty("Commands")]
            public string[] Commands =
            {
                "backpackui",
                "backpackbutton"
            };

            [JsonProperty("Background")]
            public BackgroundSettings Background = new BackgroundSettings();

            [JsonProperty("Gather mode indicator")]
            public GatherModeSettings GatherMode = new GatherModeSettings();

            [JsonProperty("Retrieve mode indicator")]
            public RetrieveModeSettings RetrieveMode = new RetrieveModeSettings();

            [JsonProperty("Occupied & total slots")]
            public SlotSettings Slots = new SlotSettings();

            [JsonProperty("Fill bar")]
            public FillBarSettings FillBar = new FillBarSettings();

            [JsonProperty("Default button position")]
            public string DefaultButtonPositionName = "Right";

            [JsonProperty("Button positions")]
            public ButtonPosition[] ButtonPositions =
            {
                new ButtonPosition
                {
                    Name = "Left",
                    OffsetX = -263.5f,
                    Url = "",
                    SkinId = 3050420442,
                    ImageSize = 56,
                },
                new ButtonPosition
                {
                    Name = "Right",
                    OffsetX = 185,
                    Url = "",
                    SkinId = 3050420772,
                    ImageSize = 56,
                },
            };

            public ButtonPosition DefaultButtonPosition { get; private set; }
            public int NumValidPositions { get; private set; }

            public void Init()
            {
                Slots.Init();
                FillBar.Init();

                var defaultIsValid = false;

                foreach (var position in ButtonPositions)
                {
                    if (!position.Enabled || position.LangKey == null)
                        continue;

                    NumValidPositions++;

                    if (StringUtils.EqualsIgnoreCase(position.Name, DefaultButtonPositionName))
                    {
                        defaultIsValid = true;
                        DefaultButtonPosition = position;
                    }
                }

                if (!string.IsNullOrWhiteSpace(DefaultButtonPositionName))
                {
                    if (!defaultIsValid)
                    {
                        LogError($"Default button position '{DefaultButtonPositionName}' does not have a corresponding enabled configuration.");
                    }

                    DefaultButtonPositionName = null;
                }

                if (NumValidPositions == 0)
                {
                    LogError("No button positions are enabled and valid. No backpack button will be displayed. Please correct your config if you intend to use this plugin.");
                }
            }

            public ButtonPosition GetButtonPosition(string positionName)
            {
                foreach (var buttonPosition in ButtonPositions)
                {
                    if (StringUtils.EqualsIgnoreCase(buttonPosition.Name, positionName))
                        return buttonPosition;
                }

                return null;
            }
        }

        private Configuration GetDefaultConfig() => new Configuration();

        #region Configuration Helpers

        [JsonObject(MemberSerialization.OptIn)]
        private class BaseConfiguration
        {
            private string ToJson() => JsonConvert.SerializeObject(this);

            public Dictionary<string, object> ToDictionary() => JsonHelper.Deserialize(ToJson()) as Dictionary<string, object>;
        }

        private static class JsonHelper
        {
            public static object Deserialize(string json) => ToObject(JToken.Parse(json));

            private static object ToObject(JToken token)
            {
                switch (token.Type)
                {
                    case JTokenType.Object:
                        return token.Children<JProperty>()
                                    .ToDictionary(prop => prop.Name,
                                                  prop => ToObject(prop.Value));

                    case JTokenType.Array:
                        return token.Select(ToObject).ToList();

                    default:
                        return ((JValue)token).Value;
                }
            }
        }

        private bool MaybeUpdateConfig(BaseConfiguration config)
        {
            var currentWithDefaults = config.ToDictionary();
            var currentRaw = Config.ToDictionary(x => x.Key, x => x.Value);
            return MaybeUpdateConfigSection(currentWithDefaults, currentRaw);
        }

        private bool MaybeUpdateConfigSection(Dictionary<string, object> currentWithDefaults, Dictionary<string, object> currentRaw)
        {
            bool changed = false;

            foreach (var key in currentWithDefaults.Keys)
            {
                object currentRawValue;
                if (currentRaw.TryGetValue(key, out currentRawValue))
                {
                    var defaultDictValue = currentWithDefaults[key] as Dictionary<string, object>;
                    var currentDictValue = currentRawValue as Dictionary<string, object>;

                    if (defaultDictValue != null)
                    {
                        if (currentDictValue == null)
                        {
                            currentRaw[key] = currentWithDefaults[key];
                            changed = true;
                        }
                        else if (MaybeUpdateConfigSection(defaultDictValue, currentDictValue))
                            changed = true;
                    }
                }
                else
                {
                    currentRaw[key] = currentWithDefaults[key];
                    changed = true;
                }
            }

            return changed;
        }

        protected override void LoadDefaultConfig() => _config = GetDefaultConfig();

        protected override void LoadConfig()
        {
            base.LoadConfig();
            try
            {
                _config = Config.ReadObject<Configuration>();
                if (_config == null)
                {
                    throw new JsonException();
                }

                var changed = MaybeUpdateConfig(_config);

                foreach (var buttonPosition in _config.ButtonPositions)
                {
                    if (buttonPosition.Url == "https://i.imgur.com/wLR9Z6V.png"
                        || buttonPosition.Url == "https://i.imgur.com/1Tep5Ad.png")
                    {
                        buttonPosition.SkinId = 3050420442;
                        buttonPosition.Url = "";
                        changed = true;
                    }
                    else if (buttonPosition.Url == "https://i.imgur.com/h1HQEAB.png"
                             || buttonPosition.Url == "https://i.imgur.com/wleeQkt.png")
                    {
                        buttonPosition.SkinId = 3050420772;
                        buttonPosition.Url = "";
                        changed = true;
                    }
                }

                if (changed)
                {
                    LogWarning("Configuration appears to be outdated; updating and saving");
                    SaveConfig();
                }
            }
            catch (Exception e)
            {
                LogError(e.Message);
                LogWarning($"Configuration file {Name}.json is invalid; using defaults");
                LoadDefaultConfig();
            }
        }

        protected override void SaveConfig()
        {
            Puts($"Configuration changes saved to {Name}.json");
            Config.WriteObject(_config, true);
        }

        #endregion

        #endregion

        private string GetMessage(string playerId, string langKey) =>
            lang.GetMessage(langKey, this, playerId);

        private string GetMessage(IPlayer player, string langKey) =>
            GetMessage(player.Id, langKey);

        private string GetMessage(BasePlayer basePlayer, string langKey) =>
            GetMessage(basePlayer.UserIDString, langKey);

        protected override void LoadDefaultMessages()
        {
            var messages = new Dictionary<string, string>
            {
                ["Usage"] = "Usage: <color=#fd4>{0}</color> <position>\n\nPositions: {1}",
                ["Off"] = "Off",
                ["Toggle"] = "Toggle",
                ["No Permission"] = "You don't have permission to use this command.",
            };

            foreach (var position in _config.ButtonPositions)
            {
                if (string.IsNullOrWhiteSpace(position.LangKey))
                    continue;

                messages[position.LangKey] = position.Name;
            }

            lang.RegisterMessages(messages, this);
        }
    }
}
