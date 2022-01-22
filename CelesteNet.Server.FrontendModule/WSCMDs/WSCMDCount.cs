﻿using Mono.Cecil;
using Mono.Cecil.Cil;
using MonoMod.Utils;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Celeste.Mod.CelesteNet.Server.Control {
    public class WSCMDCount : WSCMD {
        public override bool MustAuth => false;
        public int Counter;
        public override object? Run(object? input) {
            return ++Counter;
        }
    }
}
