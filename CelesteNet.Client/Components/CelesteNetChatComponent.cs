﻿using Celeste.Mod.CelesteNet.Client.Entities;
using Celeste.Mod.CelesteNet.DataTypes;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using Monocle;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using MDraw = Monocle.Draw;

namespace Celeste.Mod.CelesteNet.Client.Components {
    public class CelesteNetChatComponent : CelesteNetGameComponent {

        protected float _Time;

        public float Scale => Settings.UIScaleChat;
        protected int ScrolledFromIndex = 0;
        protected float ScrolledDistance = 0f;
        protected int skippedMsgCount = 0;

        public string PromptMessage = "";
        public Color PromptMessageColor = Color.White;

        public float? RenderPositionY { get; private set; } = null;

        protected Overlay _DummyOverlay = new PauseUpdateOverlay();

        public List<DataChat> Log = new();
        public List<DataChat> LogSpecial = new();
        public Dictionary<string, DataChat> Pending = new();
        public string Typing = "";

        public List<CommandInfo> CommandList = new()/*;
        / *
         * I was using these to debug on live server which doesn't send me command list yet*/
        {
            new() { ID = "tp", FirstArg = CompletionType.Player },
            new() { ID = "whisper", FirstArg = CompletionType.Player },
            new() { ID = "join", FirstArg = CompletionType.Channel },
            new() { ID = "channel", FirstArg = CompletionType.Channel },
            new() { ID = "emote", FirstArg = CompletionType.Emote },
            new() { ID = "e", FirstArg = CompletionType.Emote, AliasTo = "emote" },
            new() { ID = "tpon", FirstArg = (CompletionType) 5 }
        };
        //* /

        public ChatMode Mode => Active ? ChatMode.All : Settings.ShowNewMessages;

        public enum ChatMode {
            All,
            Special,
            Off
        }

        protected Vector2 ScrollPromptSize = new Vector2(
                                GFX.Gui["controls/directions/0x-1"].Width + GFX.Gui["controls/keyboard/PageUp"].Width,
                                Math.Max(GFX.Gui["controls/directions/0x-1"].Height, GFX.Gui["controls/keyboard/PageUp"].Height)
                            );
        public float ScrollFade => (int) Settings.ChatScrollFading / 2f;

        public enum ChatScrollFade {
            None = 0,
            Fast = 1,
            Smooth = 2
        }

        public List<string> Repeat = new() {
            ""
        };

        protected int _RepeatIndex = 0;
        public int RepeatIndex {
            get => _RepeatIndex;
            set {
                if (_RepeatIndex == value)
                    return;

                value = (value + Repeat.Count) % Repeat.Count;

                if (_RepeatIndex == 0 && value != 0)
                    Repeat[0] = Typing;
                Typing = Repeat[value];
                _RepeatIndex = value;
                _CursorIndex = Typing.Length;
            }
        }

        protected int _CursorIndex = 0;
        public int CursorIndex {
            get => _CursorIndex;
            set {
                if (value < 0)
                    value = 0;

                // This deliberately exceeds the Typing string's indices since the cursor
                // 1. ... at index 0 is before the first char,
                // 2. ... at Typing.Length-1 is before the last char,
                // and at Typing.Length is _after_ the last char.
                if (value > Typing.Length)
                    value = Typing.Length;

                _CursorIndex = value;
            }
        }

        protected bool _ControlHeld = false;
        protected bool _CursorMoveFast = false;

        protected float _TimeSinceCursorMove = 0;
        protected float _CursorInitialMoveDelay = 0.4f;
        protected float _CursorMoveDelay = 0.1f;

        protected bool _SceneWasPaused;
        protected int _ConsumeInput;
        protected bool _Active;
        public bool Active {
            get => _Active;
            set {
                if (_Active == value)
                    return;
                ScrolledDistance = 0f;
                ScrolledFromIndex = 0;
                SetPromptMessage(PromptMessageTypes.None);

                if (value) {
                    _SceneWasPaused = Engine.Scene.Paused;
                    Engine.Scene.Paused = true;
                    // If we're in a level, add a dummy overlay to prevent the pause menu from handling input.
                    if (Engine.Scene is Level level)
                        level.Overlay = _DummyOverlay;

                    _RepeatIndex = 0;
                    _Time = 0;
                    TextInput.OnInput += OnTextInput;
                } else {
                    Typing = "";
                    CursorIndex = 0;
                    UpdateCompletion(CompletionType.None);
                    Engine.Scene.Paused = _SceneWasPaused;
                    _ConsumeInput = 2;
                    if (Engine.Scene is Level level && level.Overlay == _DummyOverlay)
                        level.Overlay = null;
                    TextInput.OnInput -= OnTextInput;
                }

                _Active = value;
            }
        }

        protected List<string> Completion = new() {};
        public string CompletionPartial { get; private set; } = "";
        private int _CompletionSelected = -1;
        public int CompletionSelected { 
            get => _CompletionSelected; 
            private set {
                if (value == _CompletionSelected)
                    return;

                if (value < -1)
                    value = Completion.Count - 1;

                if (value >= Completion.Count)
                    value = -1;

                _CompletionSelected = value;
            }
        }
        protected CompletionType CompletionArgType;
        protected Atlas CompletionEmoteAtlas;
        private PromptMessageTypes PromptMessageType;

        public enum PromptMessageTypes {
            None = 0,
            Scroll,
            Info
        }

        public CelesteNetChatComponent(CelesteNetClientContext context, Game game)
            : base(context, game) {

            UpdateOrder = 10000;
            DrawOrder = 10100;

            Persistent = true;
        }

        public void Send(string text) {
            text = text?.Trim();
            if (string.IsNullOrEmpty(text))
                return;

            lock (Log) {
                if (Pending.ContainsKey(text))
                    return;
                DataChat msg = new() {
                    Player = Client.PlayerInfo,
                    Text = text
                };
                Pending[text] = msg;
                Log.Add(msg);
                LogSpecial.Add(msg);
                Client.Send(msg);
            }
        }

        public void Handle(CelesteNetConnection con, DataChat msg) {
            if (Client == null)
                return;

            lock (Log) {
                if (msg.Player?.ID == Client.PlayerInfo?.ID) {
                    foreach (DataChat pending in Pending.Values) {
                        Log.Remove(pending);
                        LogSpecial.Remove(pending);
                    }
                    Pending.Clear();
                }

                int index = Log.FindLastIndex(other => other.ID == msg.ID);
                if (index == -1) {
                    index = Log.FindLastIndex(other => other.ID < msg.ID);
                    if (index == -1)
                        index = Log.Count - 1;
                    Log.Insert(index + 1, msg);
                } else if (Log[index].Version <= msg.Version) {
                    Log[index] = msg;
                }
                if (msg.Color != Color.White) {
                    index = LogSpecial.FindLastIndex(other => other.ID == msg.ID);
                    if (index == -1) {
                        index = LogSpecial.FindLastIndex(other => other.ID < msg.ID);
                        if (index == -1)
                            index = LogSpecial.Count - 1;
                        LogSpecial.Insert(index + 1, msg);
                    } else if (LogSpecial[index].Version <= msg.Version) {
                        LogSpecial[index] = msg;
                    }
                }
            }
        }

        public void Handle(CelesteNetConnection con, DataCommandList commands) {
            CommandList.Clear();
            foreach (CommandInfo cmd in commands.List) {
                Logger.Log(LogLevel.INF, "chat", $"Learned about server command: {cmd.ID}{(!cmd.AliasTo.IsNullOrEmpty() ? $" (alias of {cmd.AliasTo})" : "")} ({cmd.FirstArg})");
                CommandList.Add(cmd);
            }
        }

        public override void Update(GameTime gameTime) {
            base.Update(gameTime);

            if (Client == null) {
                Active = false;
                return;
            }

            _Time += Engine.RawDeltaTime;
            _TimeSinceCursorMove += Engine.RawDeltaTime;

            Overworld overworld = Engine.Scene as Overworld;
            bool isRebinding = Engine.Scene == null ||
                Engine.Scene.Entities.FindFirst<KeyboardConfigUI>() != null ||
                Engine.Scene.Entities.FindFirst<ButtonConfigUI>() != null ||
                ((overworld?.Current ?? overworld?.Next) is OuiFileNaming naming && naming.UseKeyboardInput) ||
                ((overworld?.Current ?? overworld?.Next) is UI.OuiModOptionString stringInput && stringInput.UseKeyboardInput);

            if (!(Engine.Scene?.Paused ?? true) || isRebinding) {
                string typing = Typing;
                Active = false;
                Typing = typing;
            }

            if (!Active && !isRebinding && Settings.ButtonChat.Button.Pressed) {
                Active = true;

            } else if (Active) {
                Engine.Commands.Open = false;

                ScrolledDistance = Math.Max(0f, ScrolledDistance + (MInput.Keyboard.CurrentState[Keys.PageUp] - MInput.Keyboard.CurrentState[Keys.PageDown]) * 2f * Settings.ChatScrollSpeed);
                if (ScrolledDistance < 10f) {
                    ScrolledFromIndex = Log.Count;
                }

                _ControlHeld = MInput.Keyboard.Check(Keys.LeftControl) || MInput.Keyboard.Check(Keys.RightControl);

                if (!MInput.Keyboard.Check(Keys.Left) && !MInput.Keyboard.Check(Keys.Right)) {
                    _CursorMoveFast = false;
                    _TimeSinceCursorMove = 0;
                }

                // boolean to determine if Left or Right were already held on previous frame
                bool _directionHeldLast = MInput.Keyboard.PreviousState[Keys.Left] == KeyState.Down
                                       || MInput.Keyboard.PreviousState[Keys.Right] == KeyState.Down;

                bool _cursorCanMove = true;
                // conditions for the cursor to be moving:
                // 1. Don't apply delays on first frame Left/Right is pressed
                if (_directionHeldLast) {
                    // 2. Delay time depends on whether this is the initial delay or subsequent "scrolling" left or right
                    _cursorCanMove = _TimeSinceCursorMove > (_CursorMoveFast ? _CursorMoveDelay : _CursorInitialMoveDelay);
                }

                if (MInput.Keyboard.Pressed(Keys.Enter)) {
                    if (!string.IsNullOrWhiteSpace(Typing))
                        Repeat.Insert(1, Typing);
                    Send(Typing);
                    Active = false;

                } else if (MInput.Keyboard.Pressed(Keys.Down)) {
                    if (Completion.Count > 0) {
                        CompletionSelected--;
                    } else if (RepeatIndex > 0) {
                            RepeatIndex--;
                    }
                } else if (MInput.Keyboard.Pressed(Keys.Up)) {
                    if (Completion.Count > 0) {
                        CompletionSelected++;
                    } else if (RepeatIndex < Repeat.Count - 1) {
                        RepeatIndex++;
                    }
                } else if (MInput.Keyboard.Check(Keys.Left) && _cursorCanMove && CursorIndex > 0) {
                    if (_ControlHeld) {
                        // skip over space right before the cursor, if there is one
                        if (Typing[_CursorIndex - 1] == ' ')
                            CursorIndex--;
                        if (CursorIndex > 0) {
                            int prevWord = Typing.LastIndexOf(" ", _CursorIndex - 1);
                            CursorIndex = (prevWord < 0) ? 0 : prevWord + 1;
                        }
                    } else {
                        CursorIndex--;
                    }
                    _TimeSinceCursorMove = 0;
                    _CursorMoveFast = _directionHeldLast;
                    _Time = 0;

                } else if (MInput.Keyboard.Check(Keys.Right) && _cursorCanMove && CursorIndex < Typing.Length) {
                    if (_ControlHeld) {
                        int nextWord = Typing.IndexOf(" ", _CursorIndex);
                        CursorIndex = (nextWord < 0) ? Typing.Length : nextWord + 1;
                    } else {
                        CursorIndex++;
                    }
                    _TimeSinceCursorMove = 0;
                    _CursorMoveFast = _directionHeldLast;
                    _Time = 0;

                } else if (MInput.Keyboard.Pressed(Keys.Home)) {
                    CursorIndex = 0;

                } else if (MInput.Keyboard.Pressed(Keys.End)) {
                    CursorIndex = Typing.Length;

                } else if (Input.ESC.Released) {
                    Active = false;
                }

                if (Active) {
                    int spaceBeforeCursor = -1;
                    string completable = "";
                    if (_CursorIndex > 0) {
                        spaceBeforeCursor = Typing.LastIndexOf(" ", _CursorIndex - 1) + 1;
                        if (spaceBeforeCursor < _CursorIndex) {
                            completable = Typing.Substring(0, _CursorIndex).Substring(spaceBeforeCursor);
                        }
                    }

                    if (Typing.StartsWith("/") && !completable.IsNullOrEmpty()) {
                        int firstSpace = Typing.IndexOf(" ");
                        CommandInfo cmd = firstSpace == -1 ? null : CommandList.Where(c => c.ID == Typing.Substring(1, firstSpace - 1)).FirstOrDefault();

                        if (Typing.Substring(0, _CursorIndex).Equals(completable)) {
                            UpdateCompletion(CompletionType.Command, completable.Substring(1));
                        } else if (cmd != null) {
                            if (Typing.Substring(0, spaceBeforeCursor).Count(c => c == ' ') == 1) {
                                UpdateCompletion(cmd.FirstArg, completable);
                            } else if (cmd.FirstArg == CompletionType.Emote) {
                                UpdateCompletion(CompletionType.Emote, Typing.Substring(0, _CursorIndex).Substring(firstSpace + 1));
                            }
                        }
                    } else {
                        UpdateCompletion(CompletionType.None);
                    }
                }
            }

            if (CompletionSelected >= Completion.Count)
                CompletionSelected = Completion.Count - 1;

            // Prevent menus from reacting to player input after exiting chat.
            if (_ConsumeInput > 0) {
                Input.MenuConfirm.ConsumeBuffer();
                Input.MenuConfirm.ConsumePress();
                Input.ESC.ConsumeBuffer();
                Input.ESC.ConsumePress();
                Input.Pause.ConsumeBuffer();
                Input.Pause.ConsumePress();
                _ConsumeInput--;
            }

        }

        public void OnTextInput(char c) {
            if (!Active)
                return;

            if (c == (char) 13) {
                // Enter - send.
                // Handled in Update.

            } else if (c == (char) 8 && _CursorIndex > 0) {
                // Backspace - trim.
                if (Typing.Length > 0) {
                    int trim = 1;

                    // extra CursorIndex check since at index=1 using trim=1 is fine
                    if (_ControlHeld && _CursorIndex > 1) {
                        // adjust Ctrl+Backspace for having a space right before cursor
                        int _adjustedCursor = CursorIndex;
                        if (Typing[_CursorIndex - 1] == ' ')
                            _adjustedCursor--;
                        int prevWord = Typing.LastIndexOf(" ", _adjustedCursor - 1);
                        // if control is held and a space is found, trim from cursor back to space
                        if (prevWord >= 0)
                            trim = _adjustedCursor - prevWord;
                        // otherwise trim whole input back from cursor as it is one word
                        else
                            trim = _adjustedCursor;
                    }
                    // remove <trim> amount of characters before cursor
                    Typing = Typing.Remove(_CursorIndex - trim, trim);
                    _CursorIndex -= trim;
                }
                _RepeatIndex = 0;
                _Time = 0;

            } else if (c == (char) 127 && CursorIndex < Typing.Length) {
                // Delete - remove character after cursor.
                if (_ControlHeld && Typing[_CursorIndex] != ' ') {
                    int nextWord = Typing.IndexOf(" ", _CursorIndex);
                    // if control is held and a space is found, remove from cursor to space
                    if (nextWord >= 0) {
                        // include the found space in removal
                        nextWord++;
                        Typing = Typing.Remove(_CursorIndex, nextWord - _CursorIndex);
                    } else {
                        // otherwise remove everything after cursor
                        Typing = Typing.Substring(0, _CursorIndex);
                    }
                } else {
                    // just remove single char
                    Typing = Typing.Remove(_CursorIndex, 1);
                }
                _RepeatIndex = 0;
                _Time = 0;
            } else if (c == (char) 9) {
                // Tab - completion
                string accepted = "";
                if (Completion.Count == 1) {
                    accepted = Completion[0];
                } else if (Completion.Count > 1 && CompletionSelected >= 0 && CompletionSelected < Completion.Count) {
                    accepted = Completion[CompletionSelected];
                }

                if (!accepted.IsNullOrEmpty()) {
                    // remove the thing being completed, since we're inserting the accepted one
                    // and if "Name" matches for "na" we want to end up with "Name", not "name".
                    _CursorIndex -= CompletionPartial.Length;
                    Typing = Typing.Remove(_CursorIndex, CompletionPartial.Length);

                    if (CompletionArgType == CompletionType.Command) {
                        string aliased = CommandList.Where(cmd => cmd.AliasTo == accepted).Select(c => c.ID).FirstOrDefault();
                        if (!aliased.IsNullOrEmpty())
                            accepted = aliased;
                    }

                    int skipSpace = 0;
                    if (CompletionArgType != CompletionType.Emote || !accepted.EndsWith("/")) {
                        if (CursorIndex == Typing.Length || Typing[_CursorIndex] != ' ')
                            accepted += ' ';
                        else if (Typing[_CursorIndex] == ' ')
                            skipSpace = 1;
                    }

                    if (CursorIndex == Typing.Length) {
                        Typing += accepted;
                    } else {
                        // insert into string if cursor is not at the end
                        Typing = Typing.Insert(_CursorIndex, accepted);
                    }

                    _CursorIndex += accepted.Length + skipSpace;
                    UpdateCompletion(CompletionType.None);
                }
            } else if (!char.IsControl(c)) {
                if (CursorIndex == Typing.Length) {
                    // Any other character - append.
                    Typing += c;
                } else {
                    // insert into string if cursor is not at the end
                    Typing = Typing.Insert(_CursorIndex, c.ToString());
                }
                _CursorIndex++;
                _RepeatIndex = 0;
                _Time = 0;

                if (c == ' ')
                    UpdateCompletion(CompletionType.None);
            }
        }

        public void SetPromptMessage(PromptMessageTypes type, string msg = "", Color? color = null) {
            PromptMessageType = type;
            PromptMessage = msg;
            PromptMessageColor = color ?? Color.White;
        }

        public void UpdateCompletion(CompletionType type, string partial = "") {
            if (partial == CompletionPartial && type == CompletionArgType && Completion.Count == 0)
                return;

            partial = partial.Trim();
            CompletionPartial = partial;
            CompletionArgType = type;

            if (type == CompletionType.None) {
                Completion.Clear();
                CompletionPartial = "";
                CompletionSelected = -1;
                CompletionEmoteAtlas = null;
                return;
            }

            switch (type) {
                case CompletionType.Command:
                    if (string.IsNullOrWhiteSpace(partial)) {
                        Completion = CommandList.Where(cmd => cmd.AliasTo == "").Select(cmd => cmd.ID).ToList();
                    }
                    else {
                        IEnumerable<string> commands = CommandList.Where(cmd => cmd.ID.StartsWith(partial) && cmd.AliasTo == "").Select(cmd => cmd.ID);
                        IEnumerable<string> aliased = CommandList.Where(cmd => cmd.ID.StartsWith(partial) && cmd.AliasTo != "").Select(cmd => cmd.AliasTo);
                        Completion = commands.Union(aliased).ToList();
                    }

                    break;
                case CompletionType.Player:
                    DataPlayerInfo[] all = Client.Data.GetRefs<DataPlayerInfo>();

                    Completion = all.Select(p => p.FullName).Where(name => name.StartsWith(partial, StringComparison.InvariantCultureIgnoreCase)).ToList();
                    break;
                case CompletionType.Channel:
                    CelesteNetPlayerListComponent playerlist = (CelesteNetPlayerListComponent) Context.Components[typeof(CelesteNetPlayerListComponent)];
                    Completion = playerlist?.Channels?.List?.Select(channel => channel.Name).Where(name => name.StartsWith(partial, StringComparison.InvariantCultureIgnoreCase)).Distinct().ToList() ?? Completion;

                    break;

                case CompletionType.Emote:
                    if (partial.Length < 2)
                        CompletionEmoteAtlas = null;

                    if (CompletionEmoteAtlas == null) {
                        CompletionEmoteAtlas = GhostEmote.GetIconAtlas(ref partial);
                        partial = partial.Trim();
                    } else {
                        partial = partial.Substring(2).Trim();
                    }

                    if (CompletionEmoteAtlas != null) {
                        int lastSpace = partial.LastIndexOf(GhostEmote.IconPathsSeperator);

                        string prefix = lastSpace == -1 ? "" : partial.Substring(0, lastSpace + 1);
                        string subpartial = lastSpace == -1 ? partial : partial.Substring(lastSpace + 1);

                        SetPromptMessage(PromptMessageTypes.Info, CompletionPartial + "_/_" + partial + "_/_" + subpartial);

                        Completion = new();
                        foreach (string key in CompletionEmoteAtlas.GetTextures().Keys) {
                            string basename = key.TrimEnd("0123456789".ToCharArray());
                            string subkey = subpartial.Length == basename.Length ? key : basename;

                            int i = -1;
                            if (subpartial.Length < key.Length)
                                i = key.IndexOf('/', subpartial.Length);

                            if (i != -1)
                                subkey = key.Substring(0, i + 1);

                            subkey = subkey.Trim();

                            string full_completion = CompletionPartial.Substring(0, 2) + " " + prefix + subkey;
                            if (!Completion.Contains(full_completion) && subkey.StartsWith(subpartial))
                                Completion.Add(full_completion);
                        }
                    }
                    break;
            }
        }

        protected override void Render(GameTime gameTime, bool toBuffer) {
            float scale = Scale;
            Vector2 fontScale = Vector2.One * scale;

            RenderPositionY = null;

            lock (Log) {
                List<DataChat> log = Mode switch {
                    ChatMode.Special => LogSpecial,
                    ChatMode.Off => Dummy<DataChat>.EmptyList,
                    _ => Log,
                };

                if (log.Count > 0) {
                    DateTime now = DateTime.UtcNow;

                    float y = UI_HEIGHT - 50f * scale;
                    if (Active)
                        y -= 105f * scale;

                    float scrollOffset = ScrolledDistance;
                    float logLength = Settings.ChatLogLength;
                    int renderedCount = 0;
                    skippedMsgCount = 0;
                    int count = ScrolledFromIndex > 0 ? ScrolledFromIndex : log.Count;
                    for (int i = 0; i < count; i++) {
                        DataChat msg = log[count - 1 - i];

                        float alpha = 1f;
                        float delta = (float) (now - msg.ReceivedDate).TotalSeconds;
                        if (!Active && delta > 3f)
                            alpha = 1f - Ease.CubeIn(delta - 3f);
                        if (alpha <= 0f)
                            continue;

                        string time = msg.Date.ToLocalTime().ToLongTimeString();

                        string text = msg.ToString(true, false);

                        int lineScaleTry = 0;
                        float lineScale = scale;
                        RetryLineScale:
                        Vector2 lineFontScale = Vector2.One * lineScale;

                        Vector2 sizeTime = CelesteNetClientFontMono.Measure(time) * lineFontScale;
                        Vector2 sizeText = CelesteNetClientFont.Measure(text) * lineFontScale;
                        Vector2 size = new(sizeTime.X + 25f * scale + sizeText.X, Math.Max(sizeTime.Y - 5f * scale, sizeText.Y));

                        if ((size.X + 100f * scale) > UI_WIDTH && lineScaleTry < 4) {
                            lineScaleTry++;
                            lineScale -= scale * 0.1f;
                            goto RetryLineScale;
                        }

                        float height = 50f * scale + size.Y;

                        float cutoff = 0f;
                        if (renderedCount == 0) {
                            if (scrollOffset <= height) {
                                y += scrollOffset;
                                cutoff = scrollOffset;
                                logLength += cutoff / height;
                            } else {
                                skippedMsgCount++;
                                scrollOffset -= height;
                                continue;
                            }
                        }
                        int msgExtraLines = Math.Max(0, text.Count(c => c == '\n') - 1 - (int) (cutoff / sizeText.Y));
                        renderedCount++;

                        y -= height;

                        // fade at the bottom
                        alpha -= ScrollFade * cutoff / height;

                        // fade at the top
                        if (renderedCount >= logLength)
                            alpha -= ScrollFade * Math.Max(0, renderedCount - logLength);
                        
                        logLength -= msgExtraLines * 0.75f * (cutoff > 0f ? 1f - cutoff / height : 1f);

                        Context.RenderHelper.Rect(25f * scale, y, size.X + 50f * scale, height - cutoff, Color.Black * 0.8f * alpha);
                        CelesteNetClientFontMono.Draw(
                            time,
                            new(50f * scale, y + 20f * scale),
                            Vector2.Zero,
                            lineFontScale,
                            msg.Color * alpha * (msg.ID == uint.MaxValue ? 0.8f : 1f)
                        );
                        CelesteNetClientFont.Draw(
                            text,
                            new(75f * scale + sizeTime.X, y + 25f * scale),
                            Vector2.Zero,
                            lineFontScale,
                            msg.Color * alpha * (msg.ID == uint.MaxValue ? 0.8f : 1f)
                        );

                        RenderPositionY = y;

                        if (renderedCount >= logLength) {
                            break;
                        }
                    }

                    if (Active && renderedCount <= 1) {
                        ScrolledDistance -= scrollOffset;
                    }

                    if (Active) {
                        float x = 25f * scale;
                        y -= 2 * ScrollPromptSize.Y * scale;

                        bool scrollingUp = MInput.Keyboard.CurrentState[Keys.PageUp] == KeyState.Down && renderedCount > 1;
                        bool scrollingDown = MInput.Keyboard.CurrentState[Keys.PageDown] == KeyState.Down && ScrolledDistance > 0f;

                        RenderScrollPrompt(new(x, y), scale, scrollingUp, scrollingDown);
                    }
                }
            }

            if (Active) {
                RenderInputPrompt(
                    new(25f * scale, UI_HEIGHT - 125f * scale),
                    new(UI_WIDTH - 50f * scale, 100f * scale),
                    scale,
                    fontScale,
                    out float promptWidth
                );

                if (ScrolledFromIndex > 0)
                    skippedMsgCount += Log.Count - ScrolledFromIndex;

                if (Typing.Length > 0 && skippedMsgCount > 0) {
                    SetPromptMessage(
                        PromptMessageTypes.Scroll,
                        $"({skippedMsgCount} newer message{(skippedMsgCount > 1 ? "s" : "")} off-screen!)",
                        Color.Orange * .9f
                    );
                } else if (ScrolledFromIndex > 0 && ScrolledFromIndex < Log.Count) {
                    SetPromptMessage(
                        PromptMessageTypes.Scroll,
                        $"({Log.Count - ScrolledFromIndex} new message{(Log.Count - ScrolledFromIndex > 1 ? "s" : "")} since you scrolled up!)",
                        Color.GreenYellow
                    );
                } else if (PromptMessageType == PromptMessageTypes.Scroll) {
                    SetPromptMessage(PromptMessageTypes.None);
                }

                CelesteNetClientFont.Draw(
                    PromptMessage,
                    new(200f * scale + CelesteNetClientFont.Measure(Typing).X * scale, UI_HEIGHT - 105f * scale),
                    Vector2.Zero,
                    fontScale,
                    PromptMessageColor
                );

                RenderCompletions(new(25f * scale + promptWidth, UI_HEIGHT - 125f * scale), scale, fontScale);
            }
        }

        protected void RenderInputPrompt(Vector2 pos, Vector2 size, float scale, Vector2 fontScale, out float promptWidth) {
            Context.RenderHelper.Rect(pos.X, pos.Y, size.X, size.Y, Color.Black * 0.8f);
            pos.X += 25f * scale;
            pos.Y += 20f * scale;

            CelesteNetClientFont.Draw(
                ">",
                pos,
                Vector2.Zero,
                fontScale * new Vector2(0.5f, 1f),
                Color.White * 0.5f
            );
            promptWidth = CelesteNetClientFont.Measure(">").X * scale;
            pos.X += promptWidth;

            string text = Typing;
            CelesteNetClientFont.Draw(
                text,
                pos,
                Vector2.Zero,
                fontScale,
                Color.White
            );

            if (!Calc.BetweenInterval(_Time, 0.5f)) {
                if (CursorIndex == Typing.Length) {
                    pos.X += CelesteNetClientFont.Measure(text).X * scale;
                    CelesteNetClientFont.Draw(
                        "_",
                        pos,
                        Vector2.Zero,
                        fontScale,
                        Color.White * 0.5f
                    );
                } else {
                    // draw cursor at correct location, but move back half a "." width to not overlap following char
                    pos.X += CelesteNetClientFont.Measure(Typing.Substring(0, CursorIndex)).X * scale;
                    pos.X -= CelesteNetClientFont.Measure(".").X / 2f * scale;
                    pos.Y += 5f * scale;

                    CelesteNetClientFont.Draw(
                        "|",
                        pos,
                        Vector2.Zero,
                        fontScale * new Vector2(.5f, 1.2f),
                        Color.White * 0.6f
                    );
                }
            }
        }

        protected void RenderScrollPrompt(Vector2 pos, float scale, bool upActive, bool downActive) {
            Context.RenderHelper.Rect(pos.X, pos.Y, 50f * scale + ScrollPromptSize.X * scale, 2 * ScrollPromptSize.Y * scale, Color.Black * 0.8f);
            pos.X += 25f * scale;

            float oldPosX = pos.X;

            // top
            GFX.Gui["controls/keyboard/PageUp"].Draw(
                pos,
                Vector2.Zero,
                upActive ? Color.Goldenrod : Color.White,
                scale
            );
            pos.X += GFX.Gui["controls/keyboard/PageUp"].Width * scale;

            GFX.Gui["controls/directions/0x-1"].Draw(
                pos,
                Vector2.Zero,
                Color.White * (upActive ? 1f : .7f),
                scale
            );

            pos.X = oldPosX;
            pos.Y += ScrollPromptSize.Y * scale;

            // bottom
            GFX.Gui["controls/keyboard/PageDown"].Draw(
                pos,
                Vector2.Zero,
                downActive ? Color.Goldenrod : Color.White,
                scale
            );
            pos.X += GFX.Gui["controls/keyboard/PageDown"].Width * scale;

            GFX.Gui["controls/directions/0x1"].Draw(
                pos,
                Vector2.Zero,
                Color.White * (downActive ? 1f : .7f),
                scale
            );
        }

        protected void RenderCompletions(Vector2 pos, float scale, Vector2 fontScale) {
            Vector2 curPos = pos;

            for (int i = 0; i < Completion.Count; i++) {
                string match = Completion[i];

                string typed = "", suggestion = "";
                if (match.StartsWith(CompletionPartial, StringComparison.InvariantCultureIgnoreCase)) {
                    typed = match.Substring(0, CompletionPartial.Length);
                    suggestion = match.Substring(CompletionPartial.Length);
                } else {
                    suggestion = match;
                }

                string padding = Typing.Substring(0, _CursorIndex - CompletionPartial.Length);
                Vector2 sizePadding = CelesteNetClientFont.Measure(padding);
                Vector2 sizeTyped = CelesteNetClientFont.Measure(typed);
                Vector2 sizeSuggestion = CelesteNetClientFont.Measure(suggestion);

                float width = sizePadding.X + sizeTyped.X + sizeSuggestion.X + 50f;
                float height = 5f + Math.Max(sizeTyped.Y, sizeSuggestion.Y);

                curPos.X = pos.X;
                curPos.Y -= height * scale;

                Context.RenderHelper.Rect(curPos.X, curPos.Y, width * scale, height * scale, Color.Black * 0.8f);
                curPos.X += 25f * scale;

                CelesteNetClientFont.Draw(
                    padding,
                    curPos,
                    Vector2.Zero,
                    fontScale,
                    Color.Gray
                );
                curPos.X += sizePadding.X * scale;

                CelesteNetClientFont.Draw(
                    typed,
                    curPos,
                    Vector2.Zero,
                    fontScale,
                    CompletionSelected == i ? Color.LightGray : Color.Gray
                );
                curPos.X += sizeTyped.X * scale;

                CelesteNetClientFont.Draw(
                    suggestion,
                    curPos,
                    Vector2.Zero,
                    fontScale,
                    CompletionSelected == i ? Color.GreenYellow :
                    (CompletionSelected == -1 ? Color.Gold : Color.Lerp(Color.Gold, Color.LightGray, .5f))
                );
            }
        }

        protected override void Dispose(bool disposing) {
            if (Active)
                Active = false;

            base.Dispose(disposing);
        }

    }
}
