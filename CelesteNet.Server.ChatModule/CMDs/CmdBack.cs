﻿using Celeste.Mod.CelesteNet.DataTypes;
using MonoMod.Utils;
using System;

namespace Celeste.Mod.CelesteNet.Server.Chat.Cmd {
    public class CmdBack : ChatCmd {

        public override string Args => "";

        public override string Info => "Teleport to where you were before your last teleport.";

        public override void ParseAndRun(CmdEnv env) {
            CelesteNetPlayerSession? self = env.Session;
            if (self == null || env.Player == null)
                throw new Exception("Are you trying to TP as the server?");

            DynamicData selfData = new(self);
            TPHistoryEntry? back = selfData.Get<TPHistoryEntry>("tpHistory");
            if (back?.State == null || back?.Session == null)
                throw new Exception("Got nowhere to teleport back to.");
            selfData.Set("tpHistory", null);

            DataChat? msg = env.Send("Teleporting back");

            self.Con.Send(new DataMoveTo {
                SID = back.State.SID,
                Mode = back.State.Mode,
                Level = back.State.Level,
                Session = back.Session,
                Position = back.Position
            });

            if (msg != null) {
                msg.Text = "Teleported back";
                Chat.ForceSend(msg);
            }
        }

    }
}
