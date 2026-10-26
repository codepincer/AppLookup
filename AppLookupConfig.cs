using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using ArchiSteamFarm.Core;
using ArchiSteamFarm.Plugins.Interfaces;
using ArchiSteamFarm.Steam;
using ArchiSteamFarm.Steam.Interaction;

namespace AppLookup;

/// <summary>
///     Settings read from AppLookup.json, located next to the plugin's .dll.
///     The file is created with defaults on first load. Invalid files are logged and defaults are used.
///     Uses only async file APIs and manual JSON parsing (no reflection), because ASF's OS-specific
///     builds are trimmed and may be missing other runtime methods and features.
/// </summary>
internal sealed class AppLookupConfig {
	internal const string FileName = "AppLookup.json";
	internal const string ApiUrlEnvironmentVariable = "APPLOOKUP_API_URL";
	internal const string DefaultApiUrl = "https://www.steamcardexchange.net/api/request.php?GetInventory";

	private const string DefaultFileContent = """
	{
	  "ApiUrl": "https://www.steamcardexchange.net/api/request.php?GetInventory",
	  "CacheMinutes": 30,
	  "UseDiskCache": true,
	  "UseMemoryCache": false,
	  "MaxResults": 3,
	  "Aliases": ["applookup", "look"],
	  "MinimumAccess": "Operator"
	}
	""";

	private static readonly JsonDocumentOptions ParseOptions = new() {
		AllowTrailingCommas = true,
		CommentHandling = JsonCommentHandling.Skip
	};

	public string ApiUrl { get; set; } = DefaultApiUrl;
	public int CacheMinutes { get; set; } = 30;
	public bool UseDiskCache { get; set; } = true;
	public bool UseMemoryCache { get; set; } = false;
	public int MaxResults { get; set; } = 3;
	public List<string> Aliases { get; set; } = ["applookup", "look"];
	public string MinimumAccess { get; set; } = "Operator";

	internal static AppLookupConfig Current { get; private set; } = new();

	/// <summary>Folder containing the plugin .dll, set by LoadAsync. Config and disk cache live here.</summary>
	internal static string DirectoryPath { get; private set; } = "";

	internal EAccess RequiredAccess => Enum.TryParse(MinimumAccess, true, out EAccess parsed) ? parsed : EAccess.Operator;

	internal static async Task LoadAsync() {
		string directory = Path.GetDirectoryName(typeof(AppLookupConfig).Assembly.Location) ?? "";

		if (string.IsNullOrEmpty(directory)) {
			directory = AppContext.BaseDirectory;
		}

		DirectoryPath = directory;

		string path = Path.Combine(directory, FileName);
		AppLookupConfig config = new();

		try {
			if (!File.Exists(path)) {
				await File.WriteAllTextAsync(path, DefaultFileContent).ConfigureAwait(false);
				ASF.ArchiLogger.LogGenericWarning($"AppLookup: created default config at {path}. Edit it and restart ASF to change settings.");
			} else {
				string json = await File.ReadAllTextAsync(path).ConfigureAwait(false);
				config = Parse(json);
			}
		} catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException) {
			ASF.ArchiLogger.LogGenericError($"AppLookup: could not read {path} ({e.Message}), using defaults.");
			config = new AppLookupConfig();
		}

		// Environment variable overrides the file
		string? env = Environment.GetEnvironmentVariable(ApiUrlEnvironmentVariable);

		if (!string.IsNullOrWhiteSpace(env)) {
			config.ApiUrl = env.Trim();
		}

		// Sanitize values
		config.ApiUrl = config.ApiUrl.Trim();

		if (config.ApiUrl.Length == 0) {
			config.ApiUrl = DefaultApiUrl; // empty means "use the default"
		}
		config.CacheMinutes = Math.Max(1, config.CacheMinutes);

		if (!config.UseMemoryCache && !config.UseDiskCache) {
			ASF.ArchiLogger.LogGenericWarning("AppLookup: UseMemoryCache and UseDiskCache are both off, so the list would be downloaded on every command. Turning UseDiskCache on.");
			config.UseDiskCache = true;
		}
		config.MaxResults = Math.Clamp(config.MaxResults, 1, 20);

		if (config.Aliases.Count == 0) {
			config.Aliases = ["applookup", "look"];
		}

		if (!Enum.TryParse(config.MinimumAccess, true, out EAccess _)) {
			ASF.ArchiLogger.LogGenericWarning($"AppLookup: unknown MinimumAccess \"{config.MinimumAccess}\", using Operator.");
			config.MinimumAccess = "Operator";
		}

		Current = config;
	}

	private static AppLookupConfig Parse(string json) {
		AppLookupConfig config = new();

		using JsonDocument doc = JsonDocument.Parse(json, ParseOptions);

		if (doc.RootElement.ValueKind != JsonValueKind.Object) {
			throw new JsonException("root must be a JSON object");
		}

		foreach (JsonProperty property in doc.RootElement.EnumerateObject()) {
			switch (property.Name.ToLowerInvariant()) {
				case "apiurl" when property.Value.ValueKind == JsonValueKind.String:
					config.ApiUrl = property.Value.GetString() ?? "";

					break;
				case "cacheminutes" when property.Value.TryGetInt32(out int cacheMinutes):
					config.CacheMinutes = cacheMinutes;

					break;
				case "usediskcache" when property.Value.ValueKind is JsonValueKind.True or JsonValueKind.False:
					config.UseDiskCache = property.Value.ValueKind == JsonValueKind.True;

					break;
				case "usememorycache" when property.Value.ValueKind is JsonValueKind.True or JsonValueKind.False:
					config.UseMemoryCache = property.Value.ValueKind == JsonValueKind.True;

					break;
				case "maxresults" when property.Value.TryGetInt32(out int maxResults):
					config.MaxResults = maxResults;

					break;
				case "minimumaccess" when property.Value.ValueKind == JsonValueKind.String:
					config.MinimumAccess = property.Value.GetString() ?? "Operator";

					break;
				case "aliases" when property.Value.ValueKind == JsonValueKind.Array:
					List<string> aliases = [];

					foreach (JsonElement item in property.Value.EnumerateArray()) {
						string? alias = item.ValueKind == JsonValueKind.String ? item.GetString() : null;

						if (!string.IsNullOrWhiteSpace(alias)) {
							aliases.Add(alias.Trim());
						}
					}

					config.Aliases = aliases;

					break;
			}
		}

		return config;
	}
}