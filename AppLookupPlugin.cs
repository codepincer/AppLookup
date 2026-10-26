using System;
using System.Collections.Generic;
using System.Composition;
using System.Text;
using System.Threading.Tasks;
using ArchiSteamFarm.Plugins.Interfaces;
using ArchiSteamFarm.Steam;
using ArchiSteamFarm.Steam.Interaction;

namespace AppLookup;

[Export(typeof(IPlugin))]
public sealed class AppLookupPlugin : IBotCommand2 {
	// Command names (main command plus aliases) come from AppLookup.json, case-insensitive
	private static HashSet<string> CommandNames = new(StringComparer.OrdinalIgnoreCase) { "applookup", "look" };

	private const string RefreshSuffix = "refresh";
	private const int RefreshCooldownSeconds = 30;

	private static DateTime LastRefresh = DateTime.MinValue;

	public string Name => "AppLookup";

	public Version Version => typeof(AppLookupPlugin).Assembly.GetName().Version ?? throw new InvalidOperationException(nameof(Version));

	public async Task OnLoaded() {
		await AppLookupConfig.LoadAsync().ConfigureAwait(false);

		CommandNames = new HashSet<string>(AppLookupConfig.Current.Aliases, StringComparer.OrdinalIgnoreCase);

		ArchiSteamFarm.Core.ASF.ArchiLogger.LogGenericInfo($"{Name} {Version} loaded. Commands: {string.Join(", ", CommandNames)}");
	}

	public async Task<string?> OnBotCommand(Bot bot, EAccess access, string message, string[] args, ulong steamID = 0) {
		ArgumentNullException.ThrowIfNull(bot);
		ArgumentNullException.ThrowIfNull(args);

		// "<command>refresh", e.g. !lookrefresh: re-downloads the list now (Master only, with a short cooldown)
		if ((args.Length > 0) && args[0].EndsWith(RefreshSuffix, StringComparison.OrdinalIgnoreCase) && CommandNames.Contains(args[0][..^RefreshSuffix.Length])) {
			if (access < EAccess.Master) {
				return null;
			}

			double wait = RefreshCooldownSeconds - (DateTime.UtcNow - LastRefresh).TotalSeconds;

			if (wait > 0) {
				return bot.Commands.FormatBotResponse($"Please wait {Math.Ceiling(wait)}s before refreshing again.");
			}

			LastRefresh = DateTime.UtcNow;

			int? loaded = await SteamLookup.RefreshAsync(bot).ConfigureAwait(false);

			return bot.Commands.FormatBotResponse(loaded != null ? $"Refreshed: {loaded} apps loaded." : $"Refresh failed (see the ASF log), keeping the previous list.");
		}

		if ((args.Length == 0) || !CommandNames.Contains(args[0])) {
			return null; // not our command, let ASF / other plugins handle it
		}

		if (access < AppLookupConfig.Current.RequiredAccess) {
			return null; // behave like any other command the user isn't allowed to run
		}

		if (args.Length < 2) {
			return bot.Commands.FormatBotResponse("Usage: applookup <appID|name>");
		}

		// Everything after the command is the query, so names with spaces work without quotes
		string query = string.Join(' ', args, 1, args.Length - 1).Trim();

		List<AppResult> apps = [];

		if (uint.TryParse(query, out uint appID)) {
			AppResult? app = await SteamLookup.GetByIdAsync(bot, appID).ConfigureAwait(false);

			if (app != null) {
				apps.Add(app.Value);
			}
		} else {
			apps = await SteamLookup.SearchByNameAsync(bot, query, AppLookupConfig.Current.MaxResults).ConfigureAwait(false);
		}

		if (apps.Count == 0) {
			return bot.Commands.FormatBotResponse(SteamLookup.IsConfigured ? $"No app found for \"{query}\"." : $"API link not configured. Set \"ApiUrl\" in {AppLookupConfig.FileName} and restart ASF.");
		}

		StringBuilder sb = new();

		foreach (AppResult app in apps) {
			sb.AppendLine();
			sb.Append($"{app.AppID} | {app.Name} | {(app.Marketable ? "Marketable" : "Non-marketable")}");
		}

		return bot.Commands.FormatBotResponse(sb.ToString());
	}
}