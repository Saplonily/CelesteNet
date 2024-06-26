﻿using Celeste.Mod.CelesteNet.DataTypes;

namespace Celeste.Mod.CelesteNet.Server.Chat.Cmd {
    public class CmdE : CmdEmote {

        public override string Info => $"Alias for {Chat.Settings.CommandPrefix}{Chat.Commands.Get<CmdEmote>().ID}";

    }

    public class CmdEmote : ChatCmd {

        public override string Args => "<text> | i:<img> | p:<img> | g:<img>";

        public override CompletionType Completion => CompletionType.Emote;

        public override string Info => "Send an emote appearing over your player.";
        public override string Help =>
@"Send an emote appearing over your player.
Normal text appears over your player.
This syntax also works for your ""favorites"" (settings file).
i:TEXTURE shows TEXTURE from the GUI atlas.
p:TEXTURE shows TEXTURE from the Portraits atlas.
g:TEXTURE shows TEXTURE from the Gameplay atlas.
p:FRM1 FRM2 FRM3 plays an animation, 7 FPS by default.
p:10 FRM1 FRM2 FRM3 plays the animation at 10 FPS.";

        public override void ParseAndRun(CmdEnv env) {
            if (env.Session == null || string.IsNullOrWhiteSpace(env.Text))
                return;

            DataEmote emote = new() {
                Player = env.Player,
                Text = env.Text.Trim()
            };
            env.Session.Con.Send(emote);
            env.Server.Data.Handle(env.Session.Con, emote);
        }

    }
}
