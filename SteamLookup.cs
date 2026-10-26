using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ArchiSteamFarm.Steam;

namespace AppLookup;

internal readonly record struct AppResult(uint AppID, string Name, bool Marketable);

/// <summary>
///     Loads the app list from your API (flat or nested row layout, see TryParseList),
///     caches it in memory and answers lookups by app ID or name locally.
/// </summary>
internal static class SteamLookup {
	private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(30) };
	private static readonly SemaphoreSlim LoadLock = new(1, 1);

	private static Dictionary<uint, AppResult> ById = [];
	private static List<AppResult> All = [];
	private static DateTime LoadedAt = DateTime.MinValue;

	private static string ApiUrl => AppLookupConfig.Current.ApiUrl;

	private static TimeSpan CacheLifetime => TimeSpan.FromMinutes(AppLookupConfig.Current.CacheMinutes);

	/// <summary>
	///     Returns the list for one lookup. With UseMemoryCache=false the in-memory copy is dropped again right
	///     after (the file on disk is the only store), so the next lookup re-reads the saved file.
	/// </summary>
	private static async Task<(Dictionary<uint, AppResult> ById, List<AppResult> All)?> GetDataAsync(Bot bot) {
		for (int attempt = 0; attempt < 3; attempt++) {
			if (!await EnsureLoadedAsync(bot).ConfigureAwait(false)) {
				return null;
			}

			(Dictionary<uint, AppResult> ById, List<AppResult> All) data = (ById, All);

			if (!AppLookupConfig.Current.UseMemoryCache) {
				ReleaseMemory();
			}

			if (data.All.Count > 0) {
				return data;
			}

			// Another command released the list between our load and our read: just load again
		}

		return null;
	}

	private static void ReleaseMemory() {
		ById = [];
		All = [];
		LoadedAt = DateTime.MinValue;
	}

	internal static async Task<AppResult?> GetByIdAsync(Bot bot, uint appID) {
		(Dictionary<uint, AppResult> byId, List<AppResult> _)? data = await GetDataAsync(bot).ConfigureAwait(false);

		return (data != null) && data.Value.byId.TryGetValue(appID, out AppResult app) ? app : null;
	}

	/// <summary>Forces a fresh download. Returns the number of apps loaded, or null if the refresh failed (old list is kept).</summary>
	internal static async Task<int?> RefreshAsync(Bot bot) {
		DateTime before = LoadedAt;

		await EnsureLoadedAsync(bot, true).ConfigureAwait(false);

		int? loaded = LoadedAt > before ? All.Count : null;

		if (!AppLookupConfig.Current.UseMemoryCache) {
			ReleaseMemory();
		}

		return loaded;
	}

	/// <summary>Exact name matches first, then names that contain the query. Case-insensitive.</summary>
	internal static async Task<List<AppResult>> SearchByNameAsync(Bot bot, string query, int maxResults) {
		(Dictionary<uint, AppResult> _, List<AppResult> all)? data = await GetDataAsync(bot).ConfigureAwait(false);

		if (data == null) {
			return [];
		}

		List<AppResult> snapshot = data.Value.all;

		IEnumerable<AppResult> exact = snapshot.Where(a => a.Name.Equals(query, StringComparison.OrdinalIgnoreCase));
		IEnumerable<AppResult> partial = snapshot.Where(a => !a.Name.Equals(query, StringComparison.OrdinalIgnoreCase) && a.Name.Contains(query, StringComparison.OrdinalIgnoreCase));

		return exact.Concat(partial).Take(maxResults).ToList();
	}

	/// <summary>Returns false if the data couldn't be loaded and no previous copy exists.</summary>
	private static async Task<bool> EnsureLoadedAsync(Bot bot, bool force = false) {
		if (!force && ((DateTime.UtcNow - LoadedAt) < CacheLifetime)) {
			return true;
		}

		await LoadLock.WaitAsync().ConfigureAwait(false);

		try {
			if (!force && ((DateTime.UtcNow - LoadedAt) < CacheLifetime)) {
				return true;
			}

			if (!IsConfigured) {
				return false;
			}

			// First load after a restart: try the saved copy before hitting the API
			if (!force && (All.Count == 0) && AppLookupConfig.Current.UseDiskCache) {
				(DateTime At, string Json)? saved = await ReadDiskCacheAsync().ConfigureAwait(false);

				if ((saved != null) && TryParseList(saved.Value.Json, out Dictionary<uint, AppResult> savedById, out List<AppResult> savedAll) && (savedAll.Count > 0)) {
					ById = savedById;
					All = savedAll;

					if ((DateTime.UtcNow - saved.Value.At) < CacheLifetime) {
						LoadedAt = saved.Value.At; // still fresh: no download needed

						return true;
					}

					// Stale: kept only as a fallback in case the download below fails
				}
			}

			string? json = null;

			try {
				// SendAsync + stream reading: these are the calls ASF itself uses, so they survive trimming
				using HttpRequestMessage request = new(HttpMethod.Get, new Uri(ApiUrl));
				using HttpResponseMessage response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, CancellationToken.None).ConfigureAwait(false);

				if (!response.IsSuccessStatusCode) {
					ArchiSteamFarm.Core.ASF.ArchiLogger.LogGenericError($"AppLookup: API returned HTTP {(int) response.StatusCode}.");
				} else {
					using Stream stream = await response.Content.ReadAsStreamAsync(CancellationToken.None).ConfigureAwait(false);

					byte[] bytes;

					try {
						bytes = await ReadAllAsync(stream).ConfigureAwait(false);
					} catch (MissingMethodException) {
						// Trimmed runtime lacks the async copy: fall back to the plain synchronous one (the list is small)
						bytes = ReadAllSync(stream);
					}

					json = DecodeText(bytes);
				}
			} catch (Exception e) when (e is HttpRequestException or TaskCanceledException or IOException) {
				ArchiSteamFarm.Core.ASF.ArchiLogger.LogGenericError($"AppLookup: failed to download list from API: {e.Message}");
			}

			if (string.IsNullOrEmpty(json)) {
				return All.Count > 0; // keep serving the stale copy if we have one
			}

			if (!TryParseList(json, out Dictionary<uint, AppResult> byId, out List<AppResult> all)) {
				string preview = json.Substring(0, Math.Min(200, json.Length)).Replace('\r', ' ').Replace('\n', ' ');

				ArchiSteamFarm.Core.ASF.ArchiLogger.LogGenericError($"AppLookup: API response is not in a supported format. Received {json.Length} characters, starting with: {preview}");

				return All.Count > 0;
			}

			ById = byId;
			All = all;
			LoadedAt = DateTime.UtcNow;

			if (AppLookupConfig.Current.UseDiskCache) {
				await WriteDiskCacheAsync(json, LoadedAt).ConfigureAwait(false);
			}

			return true;
		} finally {
			LoadLock.Release();
		}
	}

	private static string CachePath => Path.Combine(AppLookupConfig.DirectoryPath, "AppLookup.cache");

	/// <summary>
	///     The cache file is "{utc ticks}|{api url}", a newline, then the raw JSON. The timestamp lives inside the file (not in file
	///     metadata) and the URL is stored so a cache from a different ApiUrl is ignored.
	/// </summary>
	private static async Task<(DateTime At, string Json)?> ReadDiskCacheAsync() {
		try {
			if (!File.Exists(CachePath)) {
				return null;
			}

			string text = await File.ReadAllTextAsync(CachePath).ConfigureAwait(false);
			int newline = text.IndexOf('\n');

			if (newline < 0) {
				return null;
			}

			string header = text.Substring(0, newline).TrimEnd('\r');
			int bar = header.IndexOf('|');

			if ((bar < 0) || !long.TryParse(header.Substring(0, bar), out long ticks) || (header.Substring(bar + 1) != ApiUrl)) {
				return null;
			}

			return (new DateTime(ticks, DateTimeKind.Utc), text.Substring(newline + 1));
		} catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentOutOfRangeException) {
			ArchiSteamFarm.Core.ASF.ArchiLogger.LogGenericWarning($"AppLookup: could not read disk cache ({e.Message}), will download instead.");

			return null;
		}
	}

	private static async Task WriteDiskCacheAsync(string json, DateTime at) {
		try {
			await File.WriteAllTextAsync(CachePath, $"{at.Ticks}|{ApiUrl}\n{json}").ConfigureAwait(false);
		} catch (Exception e) when (e is IOException or UnauthorizedAccessException) {
			ArchiSteamFarm.Core.ASF.ArchiLogger.LogGenericWarning($"AppLookup: could not save disk cache ({e.Message}). Set \"UseDiskCache\" to false to stop trying.");
		}
	}

	/// <summary>Decodes using the byte-order mark if there is one (UTF-16 LE/BE, UTF-8), otherwise UTF-8.</summary>
	private static string DecodeText(byte[] bytes) {
		string text;

		if ((bytes.Length >= 2) && (bytes[0] == 0xFF) && (bytes[1] == 0xFE)) {
			text = Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);
		} else if ((bytes.Length >= 2) && (bytes[0] == 0xFE) && (bytes[1] == 0xFF)) {
			text = Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2);
		} else if ((bytes.Length >= 3) && (bytes[0] == 0xEF) && (bytes[1] == 0xBB) && (bytes[2] == 0xBF)) {
			text = Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
		} else {
			text = Encoding.UTF8.GetString(bytes);
		}

		// Last-resort cleanup for UTF-16 data that arrived without a recognizable marker
		return text.Contains('\0') ? text.Replace("\0", "") : text;
	}

	// Kept in separate methods on purpose: a missing runtime method fails the whole method it is in,
	// so isolating each approach lets the caller catch MissingMethodException and try the other one.
	private static async Task<byte[]> ReadAllAsync(Stream stream) {
		using MemoryStream memory = new();

		await stream.CopyToAsync(memory).ConfigureAwait(false);

		return memory.ToArray();
	}

	private static byte[] ReadAllSync(Stream stream) {
		using MemoryStream memory = new();

		stream.CopyTo(memory);

		return memory.ToArray();
	}

	/// <summary>
	///     Minimal parser for the app list. Accepts, inside an optional { "data": ... } wrapper, rows in either layout:
	///       flat:   [appID, "name", flag]                  (flag at index 2)
	///       nested: [[appID, "name", x, y, flag], n, ...]  (flag at index 4 of the first element)
	///     Hand-written on purpose: ASF's OS-specific builds are trimmed and lack parts of System.Text.Json.
	/// </summary>
	private static bool TryParseList(string json, out Dictionary<uint, AppResult> byId, out List<AppResult> all) {
		byId = new Dictionary<uint, AppResult>();
		all = new List<AppResult>();

		int key = json.IndexOf("\"data\"", StringComparison.Ordinal);
		int pos = json.IndexOf('[', key >= 0 ? key : 0);

		if ((pos < 0) || !TryParseArray(json, ref pos, 0, out List<object?> rows)) {
			return false;
		}

		foreach (object? item in rows) {
			if (item is not List<object?> row || (row.Count == 0)) {
				continue;
			}

			List<object?> main = row;
			int flagIndex = 2;

			if (row[0] is List<object?> inner) {
				main = inner;
				flagIndex = 4;
			}

			if ((main.Count <= flagIndex) || (main[0] is not string idText) || !uint.TryParse(idText, out uint id) || (main[1] is not string name) || (main[flagIndex] is not string flagText)) {
				continue;
			}

			AppResult app = new(id, DecodeEntities(name), (flagText == "1") || flagText.Equals("true", StringComparison.OrdinalIgnoreCase));
			byId[id] = app;
			all.Add(app);
		}

		return true;
	}

	/// <summary>The source API HTML-encodes some names (e.g. "--&gt;"); decode the common entities so searches match.</summary>
	private static string DecodeEntities(string name) {
		if (!name.Contains('&')) {
			return name;
		}

		return name.Replace("&lt;", "<").Replace("&gt;", ">").Replace("&quot;", "\"").Replace("&#39;", "'").Replace("&#x27;", "'").Replace("&apos;", "'").Replace("&amp;", "&");
	}

	/// <summary>Parses the array starting at json[pos] == '['. Elements are strings (quoted or raw tokens) or nested lists.</summary>
	private static bool TryParseArray(string json, ref int pos, int depth, out List<object?> list) {
		list = new List<object?>();

		if ((depth > 8) || (pos >= json.Length) || (json[pos] != '[')) {
			return false;
		}

		pos++;

		while (pos < json.Length) {
			while ((pos < json.Length) && ((json[pos] == ',') || char.IsWhiteSpace(json[pos]))) {
				pos++;
			}

			if (pos >= json.Length) {
				return false;
			}

			char c = json[pos];

			if (c == ']') {
				pos++;

				return true;
			}

			if (c == '[') {
				if (!TryParseArray(json, ref pos, depth + 1, out List<object?> child)) {
					return false;
				}

				list.Add(child);
			} else if (c == '"') {
				list.Add(ParseString(json, ref pos));
			} else {
				int start = pos;

				while ((pos < json.Length) && (json[pos] != ',') && (json[pos] != ']') && (json[pos] != '[') && !char.IsWhiteSpace(json[pos])) {
					pos++;
				}

				list.Add(json.Substring(start, pos - start));
			}
		}

		return false; // ran off the end without closing the array
	}

	/// <summary>Reads a JSON string starting at the opening quote; leaves pos just after the closing quote.</summary>
	private static string ParseString(string json, ref int pos) {
		pos++; // opening quote

		StringBuilder sb = new();

		while ((pos < json.Length) && (json[pos] != '"')) {
			if ((json[pos] == '\\') && ((pos + 1) < json.Length)) {
				pos++;
				char c = json[pos];

				switch (c) {
					case 'n':
						sb.Append('\n');

						break;
					case 't':
						sb.Append('\t');

						break;
					case 'r':
						sb.Append('\r');

						break;
					case 'b':
						sb.Append('\b');

						break;
					case 'f':
						sb.Append('\f');

						break;
					case 'u' when ((pos + 4) < json.Length) && int.TryParse(json.AsSpan(pos + 1, 4), System.Globalization.NumberStyles.HexNumber, null, out int code):
						sb.Append((char) code);
						pos += 4;

						break;
					default:
						sb.Append(c); // covers \" \\ \/

						break;
				}
			} else {
				sb.Append(json[pos]);
			}

			pos++;
		}

		pos++; // closing quote

		return sb.ToString();
	}

	internal static bool IsConfigured => Uri.TryCreate(ApiUrl, UriKind.Absolute, out Uri? uri) && ((uri.Scheme == Uri.UriSchemeHttp) || (uri.Scheme == Uri.UriSchemeHttps));
}