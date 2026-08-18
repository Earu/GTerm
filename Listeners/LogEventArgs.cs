using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace GTerm.Listeners
{
    internal class LogEventArgs : EventArgs
    {
        internal LogEventArgs(int type, int lvl, string grp, Color col, string msg)
        {
            this.Type = type;
            this.Level = lvl;
            this.Group = grp;
            this.Color = col;
            this.Message = msg;
        }

        internal int Type { get; private set; }
        internal int Level { get; private set; }
        internal string Group { get; private set; }
        internal Color Color { get; private set; }
        internal string Message { get; set; }

        /// <summary>
        /// Pre-rendered Spectre markup for <see cref="Message"/>, used instead of escaping it and
        /// painting it a single colour. This is how GTerm's own lines get syntax highlighting, which
        /// game output cannot have because it arrives as plain text. Only valid on a single-line
        /// message, and must carry the trailing newline itself.
        /// </summary>
        internal string? MarkupOverride { get; set; }
    }
}
