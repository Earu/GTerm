using System.Text;

namespace GTerm.MCP
{
    internal enum GameConnState
    {
        /// <summary>The GTerm module is not connected over the socket.</summary>
        Disconnected,

        /// <summary>Connected, but no Lua realm answered a probe: main menu, loading, or shutting down.</summary>
        NoSession,

        /// <summary>Connected and at least one Lua realm answered.</summary>
        Live,

        /// <summary>Connected, but never probed. We do not know which realms work.</summary>
        Unverified,
    }

    internal enum RealmReach
    {
        Unknown,
        Ok,

        /// <summary>The realm does not exist in this process right now (no server state, no client, ...).</summary>
        Unreachable,

        /// <summary>The realm exists but the game refused to run our code (sv_allowcslua, script enforcer).</summary>
        Blocked,
    }

    internal sealed class RealmState
    {
        internal RealmReach Reach { get; init; } = RealmReach.Unknown;
        internal string? Reason { get; init; }

        internal bool IsUsable => this.Reach == RealmReach.Ok;

        public override string ToString() => this.Reach switch
        {
            RealmReach.Ok => "ok",
            RealmReach.Blocked => $"BLOCKED({this.Reason})",
            RealmReach.Unreachable => $"unreachable({this.Reason})",
            _ => "unknown",
        };
    }

    internal sealed class GameStatus
    {
        internal static readonly TimeSpan StaleAfter = TimeSpan.FromSeconds(15);

        internal GameConnState State { get; init; } = GameConnState.Disconnected;
        internal bool GmodProcessRunning { get; init; }

        internal string? Map { get; init; }
        internal string? Gamemode { get; init; }
        internal int? PlayerCount { get; init; }
        internal int? MaxPlayers { get; init; }
        internal bool? IsDedicated { get; init; }
        internal bool? SinglePlayer { get; init; }

        internal RealmState ClientRealm { get; init; } = new();
        internal RealmState ServerRealm { get; init; } = new();

        internal DateTime? CapturedAt { get; init; }
        internal bool Stale { get; init; }

        /// <summary>Free-form note, e.g. why a refresh was skipped.</summary>
        internal string? Note { get; init; }

        internal bool IsStaleNow => this.Stale
            || (this.CapturedAt.HasValue && DateTime.Now - this.CapturedAt.Value > StaleAfter);

        internal RealmState RealmFor(LuaRealm realm)
            => realm == LuaRealm.Server ? this.ServerRealm : this.ClientRealm;

        /// <summary>The compact line stamped onto the front of every tool result.</summary>
        internal string ToHeader()
        {
            StringBuilder sb = new("[GTerm] ");

            switch (this.State)
            {
                case GameConnState.Disconnected:
                    sb.Append("DISCONNECTED — GMod is not connected to GTerm. ");
                    sb.Append(this.GmodProcessRunning
                        ? "GMod is running but the GTerm module has not connected; restart GMod to finish installing it."
                        : "Launch GMod with the GTerm module, then retry.");
                    return sb.ToString();

                case GameConnState.Unverified:
                    sb.Append("CONNECTED (state unverified) — call get_game_status to probe realms/map");
                    return AppendNote(sb);

                case GameConnState.NoSession:
                    sb.Append("NO_SESSION — GMod is connected but no Lua realm responded (main menu / loading). ");
                    sb.Append("Load or join a game before running commands or Lua.");
                    AppendAge(sb);
                    return AppendNote(sb);

                default:
                    sb.Append(this.IsStaleNow ? "LIVE? (stale)" : "LIVE");
                    break;
            }

            if (!string.IsNullOrEmpty(this.Map)) sb.Append(" | map=").Append(this.Map);
            if (!string.IsNullOrEmpty(this.Gamemode)) sb.Append(" | gamemode=").Append(this.Gamemode);

            if (this.PlayerCount.HasValue && this.MaxPlayers.HasValue)
                sb.Append(" | players=").Append(this.PlayerCount).Append('/').Append(this.MaxPlayers);

            if (this.IsDedicated == true) sb.Append(" | host=dedicated");
            else if (this.SinglePlayer == true) sb.Append(" | host=singleplayer");

            sb.Append(" | realms=client:").Append(this.ClientRealm)
              .Append(",server:").Append(this.ServerRealm);

            AppendAge(sb);

            if (this.IsStaleNow) sb.Append(" — may be outdated; call get_game_status to refresh");

            return AppendNote(sb);
        }

        /// <summary>The expanded form returned by get_game_status.</summary>
        internal string ToDetail()
        {
            StringBuilder sb = new();
            sb.AppendLine("Garry's Mod Status");
            sb.AppendLine("==================");
            sb.AppendLine($"State:            {this.State}");
            sb.AppendLine($"GMod process:     {(this.GmodProcessRunning ? "running" : "not running")}");
            sb.AppendLine($"Map:              {this.Map ?? "(unknown)"}");
            sb.AppendLine($"Gamemode:         {this.Gamemode ?? "(unknown)"}");
            sb.AppendLine($"Players:          {(this.PlayerCount.HasValue ? $"{this.PlayerCount}/{this.MaxPlayers}" : "(unknown)")}");
            sb.AppendLine($"Dedicated server: {Describe(this.IsDedicated)}");
            sb.AppendLine($"Singleplayer:     {Describe(this.SinglePlayer)}");
            sb.AppendLine();
            sb.AppendLine("Lua realms");
            sb.AppendLine("----------");
            sb.AppendLine($"client: {this.ClientRealm}");
            sb.AppendLine($"server: {this.ServerRealm}");
            sb.AppendLine("menu:   unreachable(no console command reaches the menu realm)");
            sb.AppendLine();
            sb.AppendLine(this.CapturedAt.HasValue
                ? $"Snapshot taken {Age(this.CapturedAt.Value)} ago{(this.IsStaleNow ? " (stale)" : "")}"
                : "Never probed the running game.");

            if (!string.IsNullOrEmpty(this.Note)) sb.AppendLine($"Note: {this.Note}");

            if (this.ClientRealm.Reach == RealmReach.Blocked)
            {
                sb.AppendLine();
                sb.AppendLine("Client-realm Lua is blocked. sv_allowcslua defaults to 0 and gates lua_run_cl /");
                sb.AppendLine("lua_openscript_cl. Set 'sv_allowcslua 1' if you own the server, or use realm=server.");
            }

            return sb.ToString();
        }

        private static string Describe(bool? value) => value switch
        {
            true => "yes",
            false => "no",
            _ => "(unknown)",
        };

        private static string Age(DateTime at)
        {
            TimeSpan delta = DateTime.Now - at;
            return delta.TotalSeconds < 90
                ? $"{delta.TotalSeconds:F0}s"
                : $"{delta.TotalMinutes:F0}m";
        }

        private void AppendAge(StringBuilder sb)
        {
            if (this.CapturedAt.HasValue) sb.Append(" | snapshot=").Append(Age(this.CapturedAt.Value)).Append(" ago");
        }

        private string AppendNote(StringBuilder sb)
        {
            if (!string.IsNullOrEmpty(this.Note)) sb.Append(" | note: ").Append(this.Note);
            return sb.ToString();
        }
    }
}
