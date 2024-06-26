﻿using Celeste.Mod.CelesteNet.DataTypes;
using Microsoft.Xna.Framework;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Celeste.Mod.CelesteNet.Server.Chat.Cmd {
    public class CommandsContext : IDisposable {

        public readonly List<ChatCmd> All = new();
        public readonly Dictionary<string, ChatCmd> ByID = new();
        public readonly Dictionary<Type, ChatCmd> ByType = new();
        public readonly DataCommandList DataAll = new DataCommandList();

        public CommandsContext(ChatModule chat) {
            foreach (Type type in CelesteNetUtils.GetTypes()) {
                if (!typeof(ChatCmd).IsAssignableFrom(type) || type.IsAbstract)
                    continue;

                ChatCmd? cmd = (ChatCmd?)Activator.CreateInstance(type);
                if (cmd == null)
                    throw new Exception($"Cannot create instance of CMD {type.FullName}");
                Logger.Log(LogLevel.VVV, "chatcmds", $"Found command: {cmd.ID.ToLowerInvariant()} ({type.FullName}, {cmd.Completion})");
                All.Add(cmd);
                ByID[cmd.ID.ToLowerInvariant()] = cmd;
                ByType[type] = cmd;
            }
            DataAll.List = new CommandInfo[All.Count];

            int i = 0;
            foreach (ChatCmd cmd in All) {
                cmd.Init(chat);

                ChatCmd? aliasTo = null;
                // check if **base** type is an existing command in ByType, which means this cmd is an alias
                // N.B. the base type ChatCmd itself is abstract and shouldn't be in ByType; see above
                Type? cmdBase = cmd.GetType().BaseType;
                if (cmdBase != null)
                    ByType.TryGetValue(cmdBase, out aliasTo);

                if (aliasTo != null)
                    Logger.Log(LogLevel.VVV, "chatcmds", $"Command: {cmd.ID.ToLowerInvariant()} is {(cmd.InternalAliasing ? "internal alias" : "alias")} of {aliasTo.ID.ToLowerInvariant()}");

                DataAll.List[i++] = new CommandInfo() {
                    ID = cmd.ID,
                    Auth = cmd.MustAuth,
                    AuthExec = cmd.MustAuthExec,
                    FirstArg = cmd.Completion,
                    AliasTo = cmd.InternalAliasing ? "" : aliasTo?.ID.ToLowerInvariant() ?? ""
                };
            }

            All = All.OrderBy(cmd => cmd.HelpOrder).ToList();
        }

        public void Dispose() {
            foreach (ChatCmd cmd in All)
                cmd.Dispose();
        }

        public ChatCmd? Get(string id)
            => ByID.TryGetValue(id, out ChatCmd? cmd) ? cmd : null;

        public T? Get<T>(string id) where T : ChatCmd
            => ByID.TryGetValue(id, out ChatCmd? cmd) ? (T)cmd : null;

        public T Get<T>() where T : ChatCmd
            => ByType.TryGetValue(typeof(T), out ChatCmd? cmd) ? (T)cmd : throw new Exception($"Invalid CMD type {typeof(T).FullName}");

    }

    public abstract class ChatCmd : IDisposable {

        public static readonly char[] NameDelimiters = {
            ' ', '\n'
        };

#pragma warning disable CS8618 // Set manually after construction.
        public ChatModule Chat;
#pragma warning restore CS8618
        public virtual string ID => GetType().Name.Substring(3).ToLowerInvariant();

        public abstract string Args { get; }
        public abstract string Info { get; }
        public virtual string Help => Info;
        public virtual int HelpOrder => 0;

        public virtual bool MustAuth => false;
        public virtual bool MustAuthExec => false;

        public virtual CompletionType Completion => CompletionType.None;

        public virtual bool InternalAliasing => false;

        public virtual void Init(ChatModule chat) {
            Chat = chat;
        }

        public virtual void Dispose() {
        }

        public virtual void ParseAndRun(CmdEnv env) {
            if (MustAuth && !env.IsAuthorized || MustAuthExec && !env.IsAuthorizedExec)
                throw new Exception("Unauthorized!");

            // TODO: Improve or rewrite. This comes from GhostNet, which adopted it from disbot (0x0ade's C# Discord bot).

            string raw = env.FullText;

            int index = Chat.Settings.CommandPrefix.Length + ID.Length - 1; // - 1 because next space required
            List<CmdArg> args = new();
            while (
                index + 1 < raw.Length &&
                (index = raw.IndexOf(' ', index + 1)) >= 0
            ) {
                if (index + 1 < raw.Length && raw[index + 1] == ' ')
                    continue;

                int next = index + 1 < raw.Length ? raw.IndexOf(' ', index + 1) : -2;
                if (next < 0)
                    next = raw.Length;

                int argIndex = index + 1;
                int argLength = next - index - 1;

                // + 1 because space
                args.Add(new CmdArg(env).Parse(raw, argIndex, argLength));

                // Parse a split up range (with spaces) into a single range arg
                if (args.Count >= 3 &&
                    args[args.Count - 3].Type == CmdArgType.Int &&
                    (args[args.Count - 2].String == "-" || args[args.Count - 2].String == "+") &&
                    args[args.Count - 1].Type == CmdArgType.Int
                ) {
                    args.Add(new CmdArg(env).Parse(raw, args[args.Count - 3].Index, next - args[args.Count - 3].Index));
                    args.RemoveRange(args.Count - 4, 3);
                    continue;
                }
            }

            Run(env, args);
        }

        public virtual void Run(CmdEnv env, List<CmdArg> args) {
        }

    }

    public class CmdArg {

        public CmdEnv Env;

        public string RawText = "";
        public string String = "";
        public int Index;

        public CmdArgType Type;

        public int Int;
        public long Long;
        public ulong ULong;
        public float Float;

        public int IntRangeFrom;
        public int IntRangeTo;
        public int IntRangeMin => Math.Min(IntRangeFrom, IntRangeTo);
        public int IntRangeMax => Math.Max(IntRangeFrom, IntRangeTo);

        public CelesteNetPlayerSession? Session {
            get {
                if (Type == CmdArgType.Int || Type == CmdArgType.Long) {
                    if (Env.Server.PlayersByID.TryGetValue((uint)Long, out CelesteNetPlayerSession? session))
                        return session;
                }

                using (Env.Server.ConLock.R())
                    return
                        // check for exact name
                        Env.Server.Sessions.FirstOrDefault(session => session.PlayerInfo?.FullName.Equals(String, StringComparison.InvariantCultureIgnoreCase) ?? false) ??
                        // check for partial name in channel
                        Env.Session?.Channel.Players.FirstOrDefault(session => session.PlayerInfo?.FullName.StartsWith(String, StringComparison.InvariantCultureIgnoreCase) ?? false) ??
                        // check for partial name elsewhere
                        Env.Server.Sessions.FirstOrDefault(session => session.PlayerInfo?.FullName.StartsWith(String, StringComparison.InvariantCultureIgnoreCase) ?? false);
            }
        }

        public CmdArg(CmdEnv env) {
            Env = env;
        }

        public virtual CmdArg Parse(string raw, int index) {
            RawText = raw;
            if (index < 0 || raw.Length <= index) {
                String = "";
                Index = 0;
                return this;
            }
            String = raw.Substring(index);
            Index = index;

            return Parse();
        }
        public virtual CmdArg Parse(string raw, int index, int length) {
            RawText = raw;
            String = raw.Substring(index, length);
            Index = index;

            return Parse();
        }

        public virtual CmdArg Parse() {
            // TODO: Improve or rewrite. This comes from GhostNet, which adopted it from disbot (0x0ade's C# Discord bot).

            if (int.TryParse(String, out Int)) {
                Type = CmdArgType.Int;
                Long = IntRangeFrom = IntRangeTo = Int;
                ULong = (ulong)Int;

            } else if (long.TryParse(String, out Long)) {
                Type = CmdArgType.Long;
                ULong = (ulong)Long;

            } else if (ulong.TryParse(String, out ULong)) {
                Type = CmdArgType.ULong;

            } else if (float.TryParse(String, out Float)) {
                Type = CmdArgType.Float;
            }

            if (Type == CmdArgType.String) {
                string[] split;
                int from, to;
                if ((split = String.Split('-')).Length == 2) {
                    if (int.TryParse(split[0].Trim(), out from) && int.TryParse(split[1].Trim(), out to)) {
                        Type = CmdArgType.IntRange;
                        IntRangeFrom = from;
                        IntRangeTo = to;
                    }
                } else if ((split = String.Split('+')).Length == 2) {
                    if (int.TryParse(split[0].Trim(), out from) && int.TryParse(split[1].Trim(), out to)) {
                        Type = CmdArgType.IntRange;
                        IntRangeFrom = from;
                        IntRangeTo = from + to;
                    }
                }
            }

            return this;
        }

        public string Rest => RawText.Substring(Index);

        public override string ToString() => String;

        public static implicit operator string(CmdArg arg) => arg.String;

    }

    public enum CmdArgType {
        String,

        Int,
        IntRange,

        Long,
        ULong,

        Float,
    }

    public class CmdEnv {

        private readonly ChatModule Chat;
        public readonly DataChat Msg;

        public ChatCmd? Cmd;

        public CmdEnv(ChatModule chat, DataChat msg) {
            Chat = chat;
            Msg = msg;
        }

        public uint PlayerID => Msg.Player?.ID ?? uint.MaxValue;

        public CelesteNetServer Server => Chat.Server ?? throw new Exception("Not ready.");

        public CelesteNetPlayerSession? Session {
            get {
                if (Msg.Player == null)
                    return null;
                if (!Chat.Server.PlayersByID.TryGetValue(PlayerID, out CelesteNetPlayerSession? session))
                    return null;
                return session;
            }
        }

        public DataPlayerInfo? Player => Session?.PlayerInfo;

        public DataPlayerState? State => Chat.Server.Data.TryGetBoundRef(Player, out DataPlayerState? state) ? state : null;

        public bool IsAuthorized => !(Session?.UID?.IsNullOrEmpty() ?? true) && Chat.Server.UserData.TryLoad(Session.UID, out BasicUserInfo info) && (info.Tags.Contains(BasicUserInfo.TAG_AUTH) || info.Tags.Contains(BasicUserInfo.TAG_AUTH_EXEC));
        public bool IsAuthorizedExec => !(Session?.UID?.IsNullOrEmpty() ?? true) && Chat.Server.UserData.TryLoad(Session.UID, out BasicUserInfo info) && info.Tags.Contains(BasicUserInfo.TAG_AUTH_EXEC);

        public string FullText => Msg.Text;
        public string Text => Cmd == null ? Msg.Text : Msg.Text.Substring(Chat.Settings.CommandPrefix.Length + Cmd.ID.Length);

        public DataChat? Send(string text, string? tag = null, Color? color = null) => Chat.SendTo(Session, text, tag, color ?? Chat.Settings.ColorCommandReply);

        public DataChat? Error(Exception e) {
            string cmdName = Cmd?.ID ?? "?";

            if (e.GetType() == typeof(Exception)) {
                Logger.Log(LogLevel.VVV, "chatcmd", $"Command {cmdName} failed:\n{e}");
                return Send($"Command {cmdName} failed: {e.Message}", color: Chat.Settings.ColorError);
            }

            Logger.Log(LogLevel.ERR, "chatcmd", $"Command {cmdName} failed:\n{e}");
            return Send($"Command {cmdName} failed due to an internal error.", color: Chat.Settings.ColorError);
        }

    }

}
