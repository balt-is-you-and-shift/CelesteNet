﻿using Celeste.Mod.CelesteNet.DataTypes;
using Microsoft.Xna.Framework;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Celeste.Mod.CelesteNet.Server.Chat {
    public class ChatModule : CelesteNetServerModule<ChatSettings> {

        public readonly Dictionary<uint, DataChat> ChatLog = new Dictionary<uint, DataChat>();
        public readonly RingBuffer<DataChat> ChatBuffer = new RingBuffer<DataChat>(3000);
        public uint NextID = (uint) (DateTime.UtcNow.Ticks / TimeSpan.TicksPerSecond);

#pragma warning disable CS8618 // Set on init.
        public ChatCommands Commands;
#pragma warning restore CS8618

        public override void Init(CelesteNetServerModuleWrapper wrapper) {
            base.Init(wrapper);

            Commands = new ChatCommands(this);
            Server.OnSessionStart += OnSessionStart;
        }

        public override void Dispose() {
            base.Dispose();

            Commands.Dispose();

            Server.OnSessionStart -= OnSessionStart;
            lock (Server.Connections)
                foreach (CelesteNetPlayerSession session in Server.PlayersByCon.Values)
                    session.OnEnd -= OnSessionEnd;
        }

        private void OnSessionStart(CelesteNetPlayerSession session) {
            Broadcast(Settings.MessageGreeting.InjectSingleValue("player", session.PlayerInfo?.FullName ?? "???"));
            Send(session, Settings.MessageMOTD);
            session.OnEnd += OnSessionEnd;
        }

        private void OnSessionEnd(CelesteNetPlayerSession session, DataPlayerInfo? lastPlayerInfo) {
            string? fullName = lastPlayerInfo?.FullName;
            if (!fullName.IsNullOrEmpty())
                Broadcast(Settings.MessageLeave.InjectSingleValue("player", fullName));
        }

        public DataChat? PrepareAndLog(CelesteNetConnection? from, DataChat msg) {
            if (!msg.CreatedByServer) {
                if (from == null)
                    return null;

                CelesteNetPlayerSession? player;
                lock (Server.Connections)
                    if (!Server.PlayersByCon.TryGetValue(from, out player))
                        return null;

                msg.Player = player.PlayerInfo;
                if (msg.Player == null)
                    return null;

                msg.Text = msg.Text.Trim();
                if (string.IsNullOrEmpty(msg.Text))
                    return null;

                msg.Text.Replace("\r", "").Replace("\n", "");
                if (msg.Text.Length > Settings.MaxChatTextLength)
                    msg.Text = msg.Text.Substring(0, Settings.MaxChatTextLength);

                msg.Tag = "";
                msg.Color = Color.White;
            }

            if (msg.Text.Length == 0)
                return null;

            lock (ChatLog) {
                ChatLog[msg.ID = NextID++] = msg;
                ChatBuffer.Set(msg).Move(1);
            }

            msg.Date = DateTime.UtcNow;

            if (!msg.CreatedByServer)
                Logger.Log(LogLevel.INF, "chatmsg", msg.ToString());

            if (!(OnReceive?.InvokeWhileTrue(this, msg) ?? true))
                return null;

            return msg;
        }

        public event Func<ChatModule, DataChat, bool>? OnReceive;

        public void Handle(CelesteNetConnection? con, DataChat msg) {
            if (PrepareAndLog(con, msg) == null)
                return;

            if ((!msg.CreatedByServer || msg.Player == null) && msg.Text.StartsWith(Settings.CommandPrefix)) {
                if (msg.Player != null) {
                    // Player should at least receive msg ack.
                    msg.Color = Settings.ColorCommand;
                    msg.Target = msg.Player;
                    ForceSend(msg);
                }

                // TODO: Improve or rewrite. This comes from GhostNet, which adopted it from disbot (0x0ade's C# Discord bot).

                ChatCMDEnv env = new ChatCMDEnv(this, msg);

                string cmdName = env.FullText.Substring(Settings.CommandPrefix.Length);
                cmdName = cmdName.Split(ChatCMD.NameDelimiters)[0].ToLowerInvariant();
                if (cmdName.Length == 0)
                    return;

                ChatCMD? cmd = Commands.Get(cmdName);
                if (cmd != null) {
                    env.Cmd = cmd;
                    Task.Run(() => {
                        try {
                            cmd.ParseAndRun(env);
                        } catch (Exception e) {
                            if (e.GetType() == typeof(Exception)) {
                                env.Send($"Command {cmdName} failed: {e.Message}", color: Settings.ColorError);
                                Logger.Log(LogLevel.VVV, "chatcmd", $"Command {cmdName} failed:\n{e}");
                            } else {
                                env.Send($"Command {cmdName} failed due to an internal error.", color: Settings.ColorError);
                                Logger.Log(LogLevel.ERR, "chatcmd", $"Command {cmdName} failed:\n{e}");
                            }
                        }
                    });

                } else {
                    env.Send($"Command {cmdName} not found!", color: Settings.ColorError);
                }

                return;
            }

            Server.Broadcast(msg);
        }


        public DataChat Broadcast(string text, string? tag = null, Color? color = null) {
            Logger.Log(LogLevel.INF, "chat", $"Broadcasting: {text}");
            DataChat msg = new DataChat() {
                Text = text,
                Tag = tag ?? "",
                Color = color ?? Settings.ColorBroadcast
            };
            Handle(null, msg);
            return msg;
        }

        public DataChat? Send(CelesteNetPlayerSession? player, string text, string? tag = null, Color? color = null) {
            DataChat msg = new DataChat() {
                Target = player?.PlayerInfo,
                Text = text,
                Tag = tag ?? "",
                Color = color ?? Settings.ColorServer
            };
            if (player == null || msg.Target == null) {
                Logger.Log(LogLevel.INF, "chat", $"Sending to nobody: {text}");
                PrepareAndLog(null, msg);
                return null;
            }

            Logger.Log(LogLevel.INF, "chat", $"Sending to {msg.Target}: {text}");
            player.Con.Send(PrepareAndLog(null, msg));
            return msg;
        }

        public event Action<ChatModule, DataChat>? OnForceSend;

        public void ForceSend(DataChat msg) {
            Logger.Log(LogLevel.INF, "chatupd", msg.ToString());
            OnForceSend?.Invoke(this, msg);

            if (msg.Target == null) {
                Server.Broadcast(msg);
                return;
            }

            CelesteNetPlayerSession? player;
            lock (Server.Connections)
                if (!Server.PlayersByID.TryGetValue(msg.Target.ID, out player))
                    return;
            player.Con?.Send(msg);
        }

    }
}