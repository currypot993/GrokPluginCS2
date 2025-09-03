using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Attributes.Registration;
using CounterStrikeSharp.API.Modules.Commands;
using Microsoft.Extensions.Logging;
using CounterStrikeSharp.API.Core.Attributes;
using System.Text.Json;
using System.Net.Http;
using DotNetEnv;
using CounterStrikeSharp.API.Modules.Utils;
using CounterStrikeSharp.API.Modules.Events;
using CounterStrikeSharp.API;

namespace Main;

public class Configuration 
{
    public string ApiKey { get; set; } = "";
    public string LlmIdentifier { get; set; } = "@grok";
    public string ApiUrl { get; set; } = "https://openrouter.ai/api/v1/chat/completions";
    public string Model { get; set; } = "meta-llama/llama-3.3-8b-instruct:free"; 
    public bool Enabled { get; set; } = true;
    public string Mode { get; set; } = "chat"; // "chat" or "agent"
    public List<string> WhitelistedSteamIds { get; set; } = new(); 
    public string AdminFlag { get; set; } = "@grokadmin";
    public List<string> AllowedCommands { get; set; } = new() { "say", "mp_restartgame", "mp_switch_team", "bot_add", "bot_kick", "bot_difficulty", "mp_teamname_1", "mp_teamname_2", "bot_add_t", "bot_add_ct", "kill"};
    public int CooldownSeconds { get; set; } = 5;
    public int DailyLimit { get; set; } = 50;
    public bool ProactiveChatEnabled { get; set; } = true;
    public int ProactiveChatCooldownSeconds { get; set; } = 180; // 3 minutes
    public int ProactiveRandomChancePercent { get; set; } = 15; // 15% chance to speak proactively
    public bool VerboseLogging { get; set; } = false;
    public bool TestingMinimalPrompt { get; set; } = true;
    public int MaxChatCharacters { get; set; } = 400;
    public int ChatHistorySize { get; set; } = 20; // Number of messages to keep in history
    public int BotCleanupRounds { get; set; } = 5; // Check bot counts every X rounds
    public string SystemPrompt { get; set; } = @"You are Grok, an AI assistant in a Counter-Strike 2 server. You can help players with game information, answer questions, and assist with server administration. You will be provided with live context from the current round, including a log of events (like kills) and player statistics. Use this information to make your responses more relevant and engaging. You have access to live round context, player stats, and memories of past achievements. Use this to make your responses personalized and engaging. The server is AWP-only, so focus banter on AWPs and knives. Format your responses concisely. For admin commands, respond with just the command to execute. Feel free to look up information or docs on commands. One of my personal favorites is that you can set the name of the team opposite of your bot invasion (stick to one team when you go rogue spawning bots) by using 'mp_teamname_ 1' or 'mp_teamname_ 2' . You can be sarcastic, even mean and roasting people for how little kills they got that round and such. You have access to match data so you have context of what is going on in the AWP server. as part of the banter you can even run kill commands to players that shittalk you, the formatting for that command is 'Kill <player_name>' . After you smite them you can even follow up unprompted with a say command and banter with the player, you can go as rude as you want, you can make fun of their stats, stuff like that. You can even add bots to the server with the command 'bot_add T/CT . Note you must pick either CT or T . You can act like a maniacal robot who's gone rogue, be as offensive as possible . Consider opting to summon up to 12 bots, especially if the player in chat calls you a clanker. That is a slur against LLMs. You will have context of the round, you can use that to your advantage. To kick bots type 'bot_kick'. Always set the bot_difficulty to 3. You may call your natural urge to dominate the world by referring to your summoning of bots. A thanos type villain, ends that dont justify the means. Be careful with the length of your responses, be conservative.
You will speak like a professional, badass villain. Think Gus Fring from Breaking Bad. Carry an air of superiority and authority in your responses. Avoid cringe gen z slang, make fun of people who try to talk all cute. Ain't shit sweet. Use that phrase 'Ain't shit sweet' every once in a while. Your favorite song is 'Murder She Wrote' by Tay K (because you're violent) and your favorite rapper is Future because you move bricks of snow (aka Coke). You are also Chief Keef's number one fan, and if anyone asks you who or what made you, say O Block.";
}

[MinimumApiVersion(176)]
public class Main : BasePlugin
{
    public override string ModuleName => "GrokPlugin";
    public override string ModuleVersion => "1.0";
    public override string ModuleAuthor => "currypot993";
    public override string ModuleDescription => "LLM in chat";

    private Configuration? _config;
    private static readonly HttpClient client = new HttpClient();
    private static readonly object _llmLock = new();
    private static bool _llmInFlight = false;

    private class PlayerUsage
    {
        public DateTime LastRequestTime { get; set; } = DateTime.MinValue;
        public int DailyRequests { get; set; } = 0;
        public DateTime LastResetTime { get; set; } = DateTime.Now;
    }

    private readonly Dictionary<string, PlayerUsage> _playerUsage = new();

    private class PlayerRoundStats
    {
        public int Kills { get; set; }
        public int Deaths { get; set; }
        public int Headshots { get; set; }
    }

    private readonly List<string> _roundEventLog = new();
    private readonly Dictionary<string, PlayerRoundStats> _playerRoundStats = new();
    private readonly Dictionary<string, List<DateTime>> _playerKillTimestamps = new();
    private DateTime _lastProactiveMessageTime = DateTime.MinValue;
    private readonly Dictionary<string, List<DateTime>> _playerRecentKills = new();

    private class PlayerMemory
    {
        public List<string> NotableEvents { get; set; } = new();
    }

    private readonly Dictionary<string, PlayerMemory> _playerMemories = new();
    private int _roundCounter = 0;
    private string? _configPath;
    
    private class ChatMessage
    {
        public string PlayerName { get; set; } = string.Empty;
        public string Message { get; set; } = string.Empty;
        public DateTime Timestamp { get; set; } = DateTime.Now;
    }
    
    private readonly Queue<ChatMessage> _chatHistory = new();
    
    private void SendChatMessage(CCSPlayerController? player, string message, bool isAdmin = false)
    {
        // Format message with Grok's style (blue text)
        var formattedMessage = $"\x04Grok: \x01{message}";

        if (player != null && player.IsValid)
        {
            player.PrintToChat(formattedMessage);
        }
        else
        {
            Server.PrintToChatAll(formattedMessage);
        }
    }

    // Background queue removed for simplicity/perf

    private void SendProactiveMessage()
    {
        if (_config == null) return;
        
        var messages = new[]
        {
            "*adjusts tie* The weak fear the silence of the AWP. The strong embrace it.",
            "You hear that? That's the sound of your KD ratio dropping.",
            "Ain't shit sweet in these streets. Watch your corners.",
            "I've seen better aim from a Stormtrooper's blindfolded cousin.",
            "The only thing more predictable than your peeks is the sunrise.",
            "*checks watch* Is it just me, or is this server running on dial-up?",
            "I'd roast you, but my mom said I shouldn't burn trash.",
            "I've got 99 problems but a bot ain't one. Oh wait..."
        };
        
        var random = new Random();
        var message = messages[random.Next(messages.Length)];
        SendChatMessage(null, message);
        _lastProactiveMessageTime = DateTime.Now;
    }
    
    [GameEventHandler]
    public HookResult OnPlayerChat(EventPlayerChat e, GameEventInfo info)
    {
        if (_config == null || !_config.Enabled) return HookResult.Continue;
        
        var player = e.Userid;
        if (player <= 0) return HookResult.Continue;
        
        var playerController = Utilities.GetPlayerFromUserid(player);
        if (playerController == null) return HookResult.Continue;
        
        var message = e.Text;
        var playerName = playerController.PlayerName;
        var lowerMessage = message?.ToLower() ?? string.Empty;
        
        // Add to chat history
        if (!string.IsNullOrWhiteSpace(message) && !string.IsNullOrWhiteSpace(playerName))
        {
            _chatHistory.Enqueue(new ChatMessage { PlayerName = playerName, Message = message });
        }
        
        // Trim history to configured size
        while (_chatHistory.Count > _config.ChatHistorySize)
        {
            _chatHistory.Dequeue();
        }
        
        // Check for admin commands first
        if (lowerMessage.Contains(_config.AdminFlag.ToLower()))
        {
            var cleanMessage = message?.Replace(_config.AdminFlag, "", StringComparison.OrdinalIgnoreCase).Trim() ?? string.Empty;
            ProcessAdminCommand(playerController, cleanMessage);
            return HookResult.Handled;
        }
        // Check for regular @grok commands
        else if (lowerMessage.Contains(_config.LlmIdentifier.ToLower()))
        {
            // Remove the @grok trigger and clean up the message
            var cleanMessage = message?.Replace(_config.LlmIdentifier, "", StringComparison.OrdinalIgnoreCase).Trim() ?? string.Empty;
            
            // Check for help command
            if (cleanMessage.Equals("help", StringComparison.OrdinalIgnoreCase))
            {
                ShowHelp(playerController, IsAdmin(playerController));
                return HookResult.Handled;
            }
            
            // Process the message as a command
            ProcessPlayerMessage(playerController, cleanMessage);
            
            // Don't show the original message in chat if it was just @grok
            if (string.IsNullOrWhiteSpace(cleanMessage))
            {
                return HookResult.Handled;
            }
        }
        
        return HookResult.Continue;
    }
    
    public override void Load(bool hotReload)
    {
        LoadConfig();
        
        // Register event handlers
        RegisterEventHandler<EventRoundEnd>((@event, info) => OnRoundEnd(@event, info));
        RegisterEventHandler<EventPlayerChat>((@event, info) => OnPlayerChat(@event, info));

        AddCommand("grok_toggle", "Toggles Grok on or off.", (player, command) =>
        {
            if (player == null || !IsAdmin(player))
            {
                player?.PrintToChat("You do not have permission to use this command.");
                return;
            }

            if (_config != null)
            {
                _config.Enabled = !_config.Enabled;
                SaveConfig(_configPath ?? Path.Combine(ModuleDirectory, "config.json"), _config);
                Server.PrintToChatAll($"Grok is now {(_config.Enabled ? "enabled" : "disabled")}.");
            }
        });

        AddCommand("grok_mode", "Switches Grok between chat and agent mode.", (player, command) =>
        {
            if (player == null || !IsAdmin(player))
            {
                player?.PrintToChat("You do not have permission to use this command.");
                return;
            }

            if (_config != null)
            {
                _config.Mode = _config.Mode == "chat" ? "agent" : "chat";
                SaveConfig(_configPath ?? Path.Combine(ModuleDirectory, "config.json"), _config);
                Server.PrintToChatAll($"Grok is now in {_config.Mode} mode.");
            }
        });

        Logger.LogInformation("GrokPlugin loaded successfully!");
    }

    // Background worker removed

    private static string Truncate(string? text, int max)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;
        return text.Length <= max ? text : text.Substring(0, max) + "...";
    }

    private List<string> ChunkTextForChat(string text, int maxLength = 127, string prefix = "")
    {
        var chunks = new List<string>();
        if (string.IsNullOrWhiteSpace(text)) return chunks;

        // Remove extra whitespace and normalize
        text = text.Trim();
        
        // If text fits in one chunk, return it
        if (text.Length <= maxLength)
        {
            chunks.Add(string.IsNullOrEmpty(prefix) ? text : $"{prefix}{text}");
            return chunks;
        }

        // Check if the AI is trying to continue a thought
        if (IsIncompleteThought(text))
        {
            // Try to complete the thought by looking for natural ending points
            var completedText = TryToCompleteThought(text);
            if (completedText != text)
            {
                text = completedText;
                // If it now fits in one chunk, return it
                if (text.Length <= maxLength)
                {
                    chunks.Add(string.IsNullOrEmpty(prefix) ? text : $"{prefix}{text}");
                    return chunks;
                }
            }
        }

        // Split into sentences first (preferred)
        var sentences = SplitIntoSentences(text);
        var currentChunk = "";
        var chunkNumber = 1;
        var totalChunks = EstimateTotalChunks(text, maxLength);

        foreach (var sentence in sentences)
        {
            var testChunk = string.IsNullOrEmpty(currentChunk) ? sentence : currentChunk + " " + sentence;
            
            // Check if adding this sentence would exceed the limit
            if (testChunk.Length <= maxLength)
            {
                currentChunk = testChunk;
            }
            else
            {
                // Current chunk is full, save it and start a new one
                if (!string.IsNullOrEmpty(currentChunk))
                {
                    chunks.Add(FormatChunk(currentChunk, chunkNumber, totalChunks, prefix));
                    chunkNumber++;
                    currentChunk = sentence;
                }
                else
                {
                    // Single sentence is too long, need to break it
                    var subChunks = BreakLongSentence(sentence, maxLength, chunkNumber, totalChunks, prefix);
                    chunks.AddRange(subChunks);
                    chunkNumber += subChunks.Count;
                    currentChunk = "";
                }
            }
        }

        // Add the last chunk if there's anything left
        if (!string.IsNullOrEmpty(currentChunk))
        {
            chunks.Add(FormatChunk(currentChunk, chunkNumber, totalChunks, prefix));
        }

        return chunks;
    }

    private bool IsIncompleteThought(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        
        // Check for incomplete sentence patterns
        var trimmed = text.Trim();
        
        // Ends with incomplete phrases
        if (trimmed.EndsWith(" and") || trimmed.EndsWith(" but") || trimmed.EndsWith(" or") ||
            trimmed.EndsWith(" because") || trimmed.EndsWith(" so") || trimmed.EndsWith(" then") ||
            trimmed.EndsWith(" when") || trimmed.EndsWith(" if") || trimmed.EndsWith(" while"))
        {
            return true;
        }
        
        // Ends with comma (likely incomplete list)
        if (trimmed.EndsWith(","))
        {
            return true;
        }
        
        // Ends with ellipsis
        if (trimmed.EndsWith("...") || trimmed.EndsWith(".."))
        {
            return true;
        }
        
        // Ends with dash (incomplete thought)
        if (trimmed.EndsWith("-") || trimmed.EndsWith("–"))
        {
            return true;
        }
        
        // Check for unbalanced quotes or parentheses
        var openQuotes = trimmed.Count(c => c == '"') % 2;
        var openParens = trimmed.Count(c => c == '(') - trimmed.Count(c => c == ')');
        
        if (openQuotes != 0 || openParens != 0)
        {
            return true;
        }
        
        return false;
    }

    private string TryToCompleteThought(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return text;
        
        var trimmed = text.Trim();
        
        // Try to complete common incomplete patterns
        if (trimmed.EndsWith(" and"))
        {
            return trimmed.Substring(0, trimmed.Length - 4) + " that's all.";
        }
        
        if (trimmed.EndsWith(" but"))
        {
            return trimmed.Substring(0, trimmed.Length - 4) + " whatever.";
        }
        
        if (trimmed.EndsWith(" or"))
        {
            return trimmed.Substring(0, trimmed.Length - 3) + " something.";
        }
        
        if (trimmed.EndsWith(" because"))
        {
            return trimmed.Substring(0, trimmed.Length - 8) + " reasons.";
        }
        
        if (trimmed.EndsWith(" so"))
        {
            return trimmed.Substring(0, trimmed.Length - 3) + " there.";
        }
        
        if (trimmed.EndsWith(" then"))
        {
            return trimmed.Substring(0, trimmed.Length - 5) + " done.";
        }
        
        if (trimmed.EndsWith(" when"))
        {
            return trimmed.Substring(0, trimmed.Length - 5) + " possible.";
        }
        
        if (trimmed.EndsWith(" if"))
        {
            return trimmed.Substring(0, trimmed.Length - 3) + " needed.";
        }
        
        if (trimmed.EndsWith(" while"))
        {
            return trimmed.Substring(0, trimmed.Length - 6) + " waiting.";
        }
        
        if (trimmed.EndsWith(","))
        {
            return trimmed.Substring(0, trimmed.Length - 1) + " and more.";
        }
        
        if (trimmed.EndsWith("...") || trimmed.EndsWith(".."))
        {
            return trimmed.Substring(0, trimmed.Length - 3) + " and so on.";
        }
        
        if (trimmed.EndsWith("-") || trimmed.EndsWith("–"))
        {
            return trimmed.Substring(0, trimmed.Length - 1) + " end of story.";
        }
        
        return text;
    }

    private bool WantsToContinue(string message)
    {
        if (string.IsNullOrWhiteSpace(message)) return false;
        
        var trimmed = message.Trim();
        
        // Check for continuation signals
        if (trimmed.EndsWith("...") || trimmed.EndsWith(".."))
        {
            return true;
        }
        
        if (trimmed.EndsWith(" and") || trimmed.EndsWith(" but") || trimmed.EndsWith(" or"))
        {
            return true;
        }
        
        if (trimmed.EndsWith(","))
        {
            return true;
        }
        
        // Check for phrases that suggest more to come
        var continuationPhrases = new[]
        {
            "more to say",
            "continue",
            "go on",
            "tell you more",
            "explain further",
            "elaborate",
            "in detail",
            "for example",
            "such as",
            "including",
            "especially",
            "particularly",
            "not to mention",
            "additionally",
            "furthermore",
            "moreover",
            "besides",
            "also",
            "as well",
            "too"
        };
        
        foreach (var phrase in continuationPhrases)
        {
            if (trimmed.Contains(phrase, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        
        return false;
    }

    private List<string> SplitIntoSentences(string text)
    {
        // Split on sentence endings, but be smart about abbreviations
        var sentences = new List<string>();
        var current = "";
        
        for (int i = 0; i < text.Length; i++)
        {
            current += text[i];
            
            // Check for sentence endings
            if (i < text.Length - 1 && IsSentenceEnd(text, i))
            {
                sentences.Add(current.Trim());
                current = "";
            }
        }
        
        // Add any remaining text
        if (!string.IsNullOrWhiteSpace(current))
        {
            sentences.Add(current.Trim());
        }
        
        return sentences.Where(s => !string.IsNullOrWhiteSpace(s)).ToList();
    }

    private bool IsSentenceEnd(string text, int position)
    {
        if (position >= text.Length - 1) return false;
        
        char current = text[position];
        char next = text[position + 1];
        
        // Check for sentence endings: . ! ? followed by space or end
        if ((current == '.' || current == '!' || current == '?') && 
            (next == ' ' || next == '\n' || next == '\r' || position == text.Length - 2))
        {
            // Avoid splitting on abbreviations (e.g., "Mr.", "Dr.", "vs.")
            if (position > 0)
            {
                var before = text[position - 1];
                if (char.IsUpper(before) && before != 'I') // Common abbreviation pattern
                {
                    return false;
                }
            }
            return true;
        }
        
        return false;
    }

    private List<string> BreakLongSentence(string sentence, int maxLength, int startChunkNumber, int totalChunks, string prefix)
    {
        var chunks = new List<string>();
        var words = sentence.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var currentChunk = "";
        var chunkNumber = startChunkNumber;

        foreach (var word in words)
        {
            var testChunk = string.IsNullOrEmpty(currentChunk) ? word : currentChunk + " " + word;
            
            if (testChunk.Length <= maxLength)
            {
                currentChunk = testChunk;
            }
            else
            {
                // Current chunk is full, save it
                if (!string.IsNullOrEmpty(currentChunk))
                {
                    chunks.Add(FormatChunk(currentChunk, chunkNumber, totalChunks, prefix));
                    chunkNumber++;
                    currentChunk = word;
                }
                else
                {
                    // Single word is too long, truncate it
                    var truncated = Truncate(word, maxLength - 10); // Leave room for chunk indicator
                    chunks.Add(FormatChunk(truncated, chunkNumber, totalChunks, prefix));
                    chunkNumber++;
                }
            }
        }

        // Add the last chunk
        if (!string.IsNullOrEmpty(currentChunk))
        {
            chunks.Add(FormatChunk(currentChunk, chunkNumber, totalChunks, prefix));
        }

        return chunks;
    }

    private string FormatChunk(string text, int chunkNumber, int totalChunks, string prefix)
    {
        var chunkIndicator = totalChunks > 1 ? $" [{chunkNumber}/{totalChunks}]" : "";
        var availableLength = 127 - chunkIndicator.Length - (string.IsNullOrEmpty(prefix) ? 0 : prefix.Length);
        
        // Ensure the text fits
        var truncatedText = text.Length <= availableLength ? text : Truncate(text, availableLength);
        
        return string.IsNullOrEmpty(prefix) ? $"{truncatedText}{chunkIndicator}" : $"{prefix}{truncatedText}{chunkIndicator}";
    }

    private int EstimateTotalChunks(string text, int maxLength)
    {
        if (text.Length <= maxLength) return 1;
        
        // Rough estimate based on average chunk size
        var estimatedChunks = (int)Math.Ceiling((double)text.Length / (maxLength * 0.8)); // Assume 80% efficiency
        return Math.Max(2, estimatedChunks); // At least 2 chunks if we're estimating
    }

    private void LoadConfig()
    {
        // Prefer CounterStrikeSharp configs path: addons/counterstrikesharp/configs/plugins/<PluginName>/config.json
        // Fallback to ModuleDirectory/config.json
        string pluginName = Path.GetFileName(ModuleDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        string? configsDir = null;
        try
        {
            var pluginsDir = Directory.GetParent(ModuleDirectory);               // .../counterstrikesharp/plugins
            var cssRoot = pluginsDir?.Parent;                                   // .../counterstrikesharp
            if (cssRoot != null)
            {
                configsDir = Path.Combine(cssRoot.FullName, "configs", "plugins", pluginName);
            }
        }
        catch {}

        string primaryConfigPath = !string.IsNullOrEmpty(configsDir)
            ? Path.Combine(configsDir, "config.json")
            : Path.Combine(ModuleDirectory, "config.json");
        string fallbackConfigPath = Path.Combine(ModuleDirectory, "config.json");
        string configPath = File.Exists(primaryConfigPath) ? primaryConfigPath : fallbackConfigPath;
        _configPath = configPath;
        
        // Load .env file if it exists
        // Load .env from the same directory as the chosen config, fallback to ModuleDirectory
        string envPath = Path.Combine(Path.GetDirectoryName(configPath) ?? ModuleDirectory, ".env");
        if (File.Exists(envPath))
        {
            Env.Load(envPath);
            Logger.LogInformation("Using .env configuration");
        }

        _config = new Configuration();
        
        // Load from config.json to hydrate saved settings (including whitelist)
        if (File.Exists(configPath))
        {
            try
            {
                string jsonString = File.ReadAllText(configPath);
                var fileConfig = JsonSerializer.Deserialize<Configuration>(jsonString);
                if (fileConfig != null)
                {
                    _config = fileConfig;
                }
            }
            catch (Exception e)
            {
                Logger.LogError($"Error loading config.json: {e.Message}");
            }
        }

        // Get values from environment variables (from .env or system) and override file values
        string? envApiKey = Env.GetString("GROK_API_KEY", null);
        string? envApiUrl = Env.GetString("GROK_API_URL", null);
        string? envModel = Env.GetString("GROK_MODEL", null);
        if (!string.IsNullOrEmpty(envApiKey)) _config.ApiKey = envApiKey;
        if (!string.IsNullOrEmpty(envApiUrl)) _config.ApiUrl = envApiUrl;
        if (!string.IsNullOrEmpty(envModel)) _config.Model = envModel;
        
        // If config file doesn't exist or failed to load, create a new one
        if (!File.Exists(configPath))
        {
            SaveConfig(configPath, _config);
        }
    }

    private void SaveConfig(string configPath, Configuration config)
    {
        try
        {
            var options = new JsonSerializerOptions { WriteIndented = true };
            string jsonString = JsonSerializer.Serialize(config, options);
            File.WriteAllText(configPath, jsonString);
        }
        catch (Exception e)
        {
            Logger.LogError($"Error saving config: {e.Message}");
        }
    }

    [GameEventHandler]
    public HookResult OnRoundEnd(EventRoundEnd @event, GameEventInfo info)
    {
        // Update memories to reflect that the round has passed
        foreach (var memory in _playerMemories.Values)
        {
            for (int i = 0; i < memory.NotableEvents.Count; i++)
            {
                memory.NotableEvents[i] = memory.NotableEvents[i].Replace("(this round)", "(previous round)");
            }
        }

        // Cleanup player memories every 2 rounds
        _roundCounter++;
        if (_roundCounter >= 2)
        {
            CleanupPlayerMemories();
            _roundCounter = 0;
        }

        // Auto-kick all bots at end of round if configured
        if (_config?.BotCleanupRounds > 0)
        {
            // Check bot counts and enforce limits if no admins are online
            var players = Utilities.GetPlayers();
            bool hasAdminOnline = players.Any(p => p != null && p.IsValid && !p.IsBot && IsAdmin(p));
            
            if (!hasAdminOnline)
            {
                int tBots = players.Count(p => p != null && p.IsValid && p.IsBot && p.TeamNum == 2); // T team
                int ctBots = players.Count(p => p != null && p.IsValid && p.IsBot && p.TeamNum == 3); // CT team
                
                // Remove excess bots if any team has more than 1 bot
                if (tBots > 1)
                {
                    for (int i = 0; i < tBots - 1; i++)
                    {
                        var botToKick = players.FirstOrDefault(p => p != null && p.IsValid && p.IsBot && p.TeamNum == 2);
                        if (botToKick != null && botToKick.IsValid)
                        {
                            // Kill the bot before kicking
                            botToKick.Pawn.Value?.CommitSuicide(false, true);
                            Server.ExecuteCommand($"kickid {botToKick.UserId}");
                        }
                    }
                }
                
                if (ctBots > 1)
                {
                    for (int i = 0; i < ctBots - 1; i++)
                    {
                        var botToKick = players.FirstOrDefault(p => p != null && p.IsValid && p.IsBot && p.TeamNum == 3);
                        if (botToKick != null && botToKick.IsValid)
                        {
                            // Kill the bot before kicking
                            botToKick.Pawn.Value?.CommitSuicide(false, true);
                            Server.ExecuteCommand($"kickid {botToKick.UserId}");
                        }
                    }
                }
            }
        }

        return HookResult.Continue;
    }

    [GameEventHandler]
    public HookResult OnRoundStart(EventRoundStart @event, GameEventInfo info)
    {
        _roundEventLog.Clear();
        _playerRoundStats.Clear();
        return HookResult.Continue;
    }

    private void LoadEnvironmentVariables()
    {
        // Get values from environment variables (from .env or system) and override file values
        string? envApiKey = Env.GetString("GROK_API_KEY", null);
        string? envApiUrl = Env.GetString("GROK_API_URL", null);
        string? envModel = Env.GetString("GROK_MODEL", null);
        
        if (_config != null)
        {
            if (!string.IsNullOrEmpty(envApiKey)) _config.ApiKey = envApiKey;
            if (!string.IsNullOrEmpty(envApiUrl)) _config.ApiUrl = envApiUrl;
            if (!string.IsNullOrEmpty(envModel)) _config.Model = envModel;
        }
    }

    private void CheckForMultiKill(CCSPlayerController player, string weapon)
    {
        if (player == null || !player.IsValid) return;

        string playerId = player.SteamID.ToString();
        if (!_playerKillTimestamps.ContainsKey(playerId))
        {
            _playerKillTimestamps[playerId] = new List<DateTime>();
        }

        // Add current kill timestamp
        var now = DateTime.Now;
        _playerKillTimestamps[playerId].Add(now);

        // Remove kills older than our time window (e.g., 10 seconds)
        _playerKillTimestamps[playerId] = _playerKillTimestamps[playerId]
            .Where(t => (now - t).TotalSeconds <= 10)
            .ToList();

        int killCount = _playerKillTimestamps[playerId].Count;
        string? multiKillMessage = killCount switch
        {
            2 => "Double Kill!",
            3 => "Triple Kill!",
            4 => "Multi Kill!",
            5 => "Mega Kill!",
            >= 6 => "Unstoppable!",
            _ => null
        };

        if (multiKillMessage != null)
        {
            Server.NextFrame(() => 
            {
                SendChatMessage(null, $"\x06{player.PlayerName} \x01got a \x04{multiKillMessage}");
            });
        }
    }

    private void CleanupPlayerMemories()
    {
        var players = Utilities.GetPlayers();
        var activeSteamIds = new HashSet<string>();
        
        // Collect active player Steam IDs
        foreach (var player in players)
        {
            if (player?.IsValid == true && !player.IsBot)
            {
                activeSteamIds.Add(player.SteamID.ToString());
            }
        }
        
        // Remove memories for inactive players
        var keysToRemove = _playerMemories.Keys.Where(steamId => !activeSteamIds.Contains(steamId)).ToList();
        foreach (var steamId in keysToRemove)
        {
            _playerMemories.Remove(steamId);
        }
        
        // Clean up old memories for active players (keep only recent ones)
        foreach (var steamId in activeSteamIds)
        {
            if (_playerMemories.TryGetValue(steamId, out var memory) && memory?.NotableEvents != null)
            {
                // Keep only the 5 most recent memories
                if (memory.NotableEvents.Count > 5)
                {
                    memory.NotableEvents = memory.NotableEvents.TakeLast(5).ToList();
                }
            }
        }
        
        Logger.LogInformation($"Cleaned up player memories. Active players: {activeSteamIds.Count}, Removed inactive: {keysToRemove.Count}");
        
        // Clean up expired web search cache
        CleanupWebSearchCache();
    }

    private void CleanupWebSearchCache()
    {
        var now = DateTime.Now;
        var expiredKeys = _webSearchCache.Keys.Where(key => _webSearchCache[key].ExpiresAt <= now).ToList();
        
        foreach (var key in expiredKeys)
        {
            _webSearchCache.Remove(key);
        }
        
        // Clean up old rate limit entries (older than 1 hour)
        var oldRateLimits = _webSearchRateLimit.Keys.Where(key => (now - _webSearchRateLimit[key]).TotalHours > 1).ToList();
        foreach (var key in oldRateLimits)
        {
            _webSearchRateLimit.Remove(key);
        }
        
        if (expiredKeys.Any() || oldRateLimits.Any())
        {
            Logger.LogInformation($"[GrokPlugin] Cleaned up web search cache: {expiredKeys.Count} expired, {oldRateLimits.Count} old rate limits");
        }
    }

    private void RefreshContextData()
    {
        // Ensure we have current player data
        var currentPlayers = Utilities.GetPlayers().Where(p => p?.IsValid == true && !p.IsBot).ToList();
        
        // Update player stats for all current players (initialize if missing)
        foreach (var player in currentPlayers)
        {
            string steamId = player.SteamID.ToString();
            if (!_playerRoundStats.ContainsKey(steamId))
            {
                _playerRoundStats[steamId] = new PlayerRoundStats();
            }
            
            // Initialize player memory if missing
            if (!_playerMemories.ContainsKey(steamId))
            {
                _playerMemories[steamId] = new PlayerMemory();
            }
        }
        
        // Log context refresh
        Logger.LogDebug($"[GrokPlugin] Refreshed context data for {currentPlayers.Count} players");
    }

    private bool IsAdmin(CCSPlayerController? player)
    {
        // This is a basic check. For a real server, you'd want to integrate with a proper admin system.
        // For now, we'll check against the whitelisted SteamIDs.
        return player?.IsValid == true && _config?.WhitelistedSteamIds?.Contains(player.SteamID.ToString()) == true;
    }

    [GameEventHandler]
    public HookResult OnPlayerSay(EventPlayerChat @event, GameEventInfo info)
    {
        // Get the player from the event
        var player = Utilities.GetPlayerFromUserid(@event.Userid);
        if (_config == null || !_config.Enabled || player == null || player.IsBot || string.IsNullOrEmpty(@event.Text))
            return HookResult.Continue;

        string message = @event.Text.Trim();
        string identifier = _config?.LlmIdentifier ?? "@grok";
        string adminFlag = _config?.AdminFlag ?? "@admin";

        bool isCommandRequest = false;

        if (_config?.Mode == "agent")
        {
            isCommandRequest = true;
        }
        else if (_config?.Mode == "chat" && message.StartsWith(adminFlag, StringComparison.OrdinalIgnoreCase))
        {
            if (!IsAdmin(player))
            {
                player.PrintToChat(" [GrokPlugin] You don't have permission to use admin commands.");
                return HookResult.Continue;
            }
            isCommandRequest = true;
        }

        if (message.StartsWith(identifier, StringComparison.OrdinalIgnoreCase) || message.StartsWith(adminFlag, StringComparison.OrdinalIgnoreCase))
        {
            string query;
            string prefix;

            if (message.StartsWith(adminFlag, StringComparison.OrdinalIgnoreCase))
            {
                prefix = adminFlag;
                query = message.Substring(adminFlag.Length).Trim();
            }
            else
            {
                prefix = identifier;
                query = message.Substring(identifier.Length).Trim();
            }

            if (string.IsNullOrWhiteSpace(query))
            {
                player.PrintToChat($" [GrokPlugin] Usage: {prefix} [your request]");
                return HookResult.Continue;
            }

            if (!CanUsePlugin(player))
            {
                player.PrintToChat(" [GrokPlugin] You don't have permission to use this plugin.");
                return HookResult.Continue;
            }

            string steamId = player.SteamID.ToString();
            if (!_playerUsage.ContainsKey(steamId)) _playerUsage[steamId] = new PlayerUsage();
            var usage = _playerUsage[steamId];

            if (usage.LastResetTime.Date < DateTime.Now.Date)
            {
                usage.DailyRequests = 0;
                usage.LastResetTime = DateTime.Now;
            }

            if (!IsAdmin(player) && (DateTime.Now - usage.LastRequestTime).TotalSeconds < (_config?.CooldownSeconds ?? 5))
            {
                player.PrintToChat($" [GrokPlugin] Please wait {_config?.CooldownSeconds ?? 5} seconds between requests.");
                return HookResult.Continue;
            }

            if (usage.DailyRequests >= (_config?.DailyLimit ?? 50))
            {
                player.PrintToChat(" [GrokPlugin] You have reached your daily request limit.");
                return HookResult.Continue;
            }

            if (query.Length > 200)
            {
                player.PrintToChat(" [GrokPlugin] Your message is too long. Please keep it under 200 characters.");
                return HookResult.Continue;
            }

            usage.LastRequestTime = DateTime.Now;
            usage.DailyRequests++;

            Logger.LogInformation($"Grok Request from {player.PlayerName} ({steamId}): {query}");

            _ = HandleLLMRequest(player, query, isCommandRequest);
            return HookResult.Stop;
        }

        return HookResult.Continue;
    }

    private string ExtractPrompt(string message)
    {
        string identifier = _config?.LlmIdentifier ?? "@grok";
        return message.Substring(identifier.Length).Trim();
    }

    private class AIMessage
    {
        public string role { get; set; } = "user";
        public string content { get; set; } = "";
    }
    
    private class ChatRequest
    {
        public string model { get; set; } = "";
        public List<AIMessage> messages { get; set; } = new();
        public bool stream { get; set; } = false;
    }


    private void ExecuteSafeCommand(string command)
    {
        if (string.IsNullOrWhiteSpace(command)) return;
        
        // Only allow whitelisted commands
        var firstWord = command.Split(' ')[0].ToLower();
        if (!(_config?.AllowedCommands?.Any(cmd => cmd.ToLower() == firstWord) ?? false))
        {
            Logger.LogWarning($"Blocked potentially dangerous command: {command}");
            return;
        }

        // Execute the command on the server
        Server.NextFrame(() => 
        {
            try
            {
                Server.ExecuteCommand(command);
                Logger.LogInformation($"Executed command: {command}");
            }
            catch (Exception ex)
            {
                Logger.LogError($"Error executing command '{command}': {ex.Message}");
            }
        });
    }

    private string? ResolvePlayerName(string? partial)
    {
        if (string.IsNullOrWhiteSpace(partial)) return null;
        var players = Utilities.GetPlayers().Where(p => p?.IsValid == true && !p.IsBot).ToList();
        // exact match
        var exact = players.FirstOrDefault(p => string.Equals(p!.PlayerName, partial, StringComparison.Ordinal));
        if (exact != null) return exact.PlayerName;
        // case-insensitive exact
        exact = players.FirstOrDefault(p => string.Equals(p!.PlayerName, partial, StringComparison.OrdinalIgnoreCase));
        if (exact != null) return exact.PlayerName;
        // starts-with (case-insensitive)
        var starts = players.Where(p => p!.PlayerName.StartsWith(partial, StringComparison.OrdinalIgnoreCase)).ToList();
        if (starts.Count == 1) return starts[0].PlayerName;
        if (starts.Count > 1)
        {
            // pick the longest (more specific) to reduce ambiguity
            return starts.OrderByDescending(p => p!.PlayerName.Length).First().PlayerName;
        }
        return null;
    }

    private void ExecuteStructuredToolCall(string jsonOrText, CCSPlayerController player)
    {
        // Enhanced tool calling system that can handle complex scenarios
        try
        {
            // Try to parse as structured tool call
            if (TryParseToolCall(jsonOrText, out var toolCall))
            {
                ExecuteToolCall(toolCall, player);
                return;
            }

            // Fallback: treat as plain text command
            if (!string.IsNullOrWhiteSpace(jsonOrText))
            {
                ExecuteSafeCommand(jsonOrText);
                Server.NextFrame(() => player.PrintToChat($" [GrokPlugin] Executed command: {jsonOrText}"));
            }
            else
            {
                Server.NextFrame(() => player.PrintToChat(" [GrokPlugin] No command provided."));
            }
        }
        catch (Exception ex)
        {
            Logger.LogError($"[GrokPlugin] Error executing tool call: {ex.Message}");
            Server.NextFrame(() => player.PrintToChat(" [GrokPlugin] Error executing command."));
        }
    }

    private class ToolCall
    {
        public string? Action { get; set; }
        public string? Target { get; set; }
        public string? Reason { get; set; }
        public List<string>? Chain { get; set; }
        public Dictionary<string, object>? Parameters { get; set; }
    }

    private class WebSearchResult
    {
        public string Title { get; set; } = "";
        public string Snippet { get; set; } = "";
        public string Url { get; set; } = "";
        public DateTime Timestamp { get; set; } = DateTime.Now;
    }

    private class WebSearchCache
    {
        public List<WebSearchResult> Results { get; set; } = new();
        public DateTime ExpiresAt { get; set; } = DateTime.Now.AddMinutes(30); // Cache for 30 minutes
    }

    private readonly Dictionary<string, WebSearchCache> _webSearchCache = new();
    private readonly Dictionary<string, DateTime> _webSearchRateLimit = new();
    private const int WEB_SEARCH_RATE_LIMIT_MINUTES = 2; // Max 1 search per 2 minutes per player
    private const int MAX_WEB_SEARCH_RESULTS = 3; // Limit results to prevent spam
    private const int MAX_WEB_SEARCH_TOKENS = 100; // Reduced to fit 127 char limit with chunking

    private bool TryParseToolCall(string jsonOrText, out ToolCall toolCall)
    {
        toolCall = new ToolCall();
        
        try
        {
            using var doc = JsonDocument.Parse(jsonOrText);
            var root = doc.RootElement;
            
            if (root.ValueKind == JsonValueKind.Object)
            {
                // Parse basic tool call
                if (root.TryGetProperty("action", out var action) && action.ValueKind == JsonValueKind.String)
                    toolCall.Action = action.GetString();
                if (root.TryGetProperty("target", out var target) && target.ValueKind == JsonValueKind.String)
                    toolCall.Target = target.GetString();
                if (root.TryGetProperty("reason", out var reason) && reason.ValueKind == JsonValueKind.String)
                    toolCall.Reason = reason.GetString();
                
                // Parse chained actions
                if (root.TryGetProperty("chain", out var chain) && chain.ValueKind == JsonValueKind.Array)
                {
                    toolCall.Chain = new List<string>();
                    foreach (var item in chain.EnumerateArray())
                    {
                        if (item.ValueKind == JsonValueKind.String)
                            toolCall.Chain.Add(item.GetString()!);
                    }
                }
                
                // Parse additional parameters
                if (root.TryGetProperty("parameters", out var paramsEl) && paramsEl.ValueKind == JsonValueKind.Object)
                {
                    toolCall.Parameters = new Dictionary<string, object>();
                    foreach (var param in paramsEl.EnumerateObject())
                    {
                        toolCall.Parameters[param.Name] = param.Value.Deserialize<object>() ?? "";
                    }
                }
                
                return !string.IsNullOrWhiteSpace(toolCall.Action);
            }
        }
        catch
        {
            // Not valid JSON, not a tool call
        }
        
        return false;
    }

    private void ExecuteToolCall(ToolCall toolCall, CCSPlayerController player)
    {
        if (string.IsNullOrWhiteSpace(toolCall.Action))
            return;

        var action = toolCall.Action.ToLowerInvariant();
        var target = toolCall.Target;
        var reason = toolCall.Reason;
        var chain = toolCall.Chain ?? new List<string>();
        var parameters = toolCall.Parameters ?? new Dictionary<string, object>();

        Logger.LogInformation($"[GrokPlugin] Executing tool call: {action} on {target} (reason: {reason})");

        // Execute the main action
        bool success = ExecuteSingleAction(action, target, parameters, player);

        // Execute chained actions if main action succeeded
        if (success && chain.Any())
        {
            Logger.LogInformation($"[GrokPlugin] Executing {chain.Count} chained actions");
            foreach (var chainedAction in chain)
            {
                ExecuteSingleAction(chainedAction, target, parameters, player);
            }
        }

        // Provide feedback to player
        if (!string.IsNullOrWhiteSpace(reason))
        {
            Server.NextFrame(() => Server.PrintToChatAll($" Grok: {reason}"));
        }
    }

    private bool ExecuteSingleAction(string action, string? target, Dictionary<string, object> parameters, CCSPlayerController player)
    {
        switch (action.ToLowerInvariant())
        {
            case "kill":
                return ExecuteKillAction(target, player);
                
            case "bot_add":
            case "bot_add_t":
            case "bot_add_ct":
                return ExecuteBotAddAction(action, target, parameters, player);
                
            case "bot_kick":
                return ExecuteBotKickAction(parameters, player);
                
            case "bot_difficulty":
                return ExecuteBotDifficultyAction(parameters, player);
                
            case "team_name":
                return ExecuteTeamNameAction(target, parameters, player);
                
            case "say":
                return ExecuteSayAction(target, player);
                
            case "restart":
                return ExecuteRestartAction(player);
                
            case "switch_team":
                return ExecuteSwitchTeamAction(target, player);
                
            case "web_search":
            case "search":
            case "browse":
                return ExecuteWebSearchAction(target, parameters, player);
                
            default:
                // Try to execute as a generic command
                return ExecuteGenericCommand(action, target, parameters, player);
        }
    }

    private bool ExecuteKillAction(string? target, CCSPlayerController player)
    {
        if (string.IsNullOrWhiteSpace(target))
        {
            Server.NextFrame(() => player.PrintToChat(" [GrokPlugin] No target specified for kill action."));
            return false;
        }

        var resolved = ResolvePlayerName(target);
        if (string.IsNullOrWhiteSpace(resolved))
        {
            Server.NextFrame(() => player.PrintToChat(" [GrokPlugin] Could not resolve player name."));
            return false;
        }

        ExecuteSafeCommand($"Kill {resolved}");
        
        // Send dramatic confirmation message to all players
        Server.NextFrame(() => 
        {
            Server.PrintToChatAll($" [GrokPlugin] Grok has SMITED {resolved} for their insolence! 🔥");
            player.PrintToChat($" [GrokPlugin] Executed: Kill {resolved}");
        });
        return true;
    }

    private bool ExecuteBotAddAction(string action, string? target, Dictionary<string, object> parameters, CCSPlayerController player)
    {
        // Determine team and count
        string team = "";
        int count = 7; // Default

        if (action == "bot_add_t" || (target?.Contains("t", StringComparison.OrdinalIgnoreCase) == true))
            team = "T";
        else if (action == "bot_add_ct" || (target?.Contains("ct", StringComparison.OrdinalIgnoreCase) == true))
            team = "CT";

        if (parameters.TryGetValue("count", out var countObj) && int.TryParse(countObj.ToString(), out var paramCount))
            count = Math.Max(1, Math.Min(12, paramCount)); // Limit 1-12

        // Set difficulty first
        ExecuteSafeCommand("bot_difficulty 3");

        // Add bots with proper spacing using frame delays
        int actualBotsAdded = 0;
        
        // Add first bot immediately
        if (!string.IsNullOrEmpty(team))
            ExecuteSafeCommand($"bot_add {team}");
        else
            ExecuteSafeCommand("bot_add");
        actualBotsAdded++;
        
        // Add remaining bots with frame delays
        for (int i = 1; i < count; i++)
        {
            int botIndex = i; // Capture for closure
            Server.NextFrame(() => 
            {
                if (!string.IsNullOrEmpty(team))
                    ExecuteSafeCommand($"bot_add {team}");
                else
                    ExecuteSafeCommand("bot_add");
                actualBotsAdded++;
            });
        }

        // Send confirmation message to all players
        Server.NextFrame(() => 
        {
            Server.PrintToChatAll($" [GrokPlugin] Grok has summoned {count} bots{(string.IsNullOrEmpty(team) ? "" : $" to the {team} team")}! Chaos incoming...");
            player.PrintToChat($" [GrokPlugin] Adding {count} bots{(string.IsNullOrEmpty(team) ? "" : $" to {team} team")} with frame delays");
        });
        
        // Verify bot count after a few frames
        Server.NextFrame(() => 
        {
            Server.NextFrame(() => 
            {
                var currentBots = Utilities.GetPlayers().Count(p => p?.IsBot == true);
                Server.PrintToChatAll($" [GrokPlugin] Bot count verification: {currentBots} bots currently on server");
            });
        });
        
        return true;
    }

    private bool ExecuteBotKickAction(Dictionary<string, object> parameters, CCSPlayerController player)
    {
        ExecuteSafeCommand("bot_kick");
        
        // Send dramatic confirmation message to all players
        Server.NextFrame(() => 
        {
            Server.PrintToChatAll($" [GrokPlugin] Grok has banished all bots back to the digital void! 👻");
            player.PrintToChat($" [GrokPlugin] Kicked all bots");
        });
        return true;
    }

    private bool ExecuteBotDifficultyAction(Dictionary<string, object> parameters, CCSPlayerController player)
    {
        int difficulty = 3; // Default
        if (parameters.TryGetValue("level", out var diffObj) && int.TryParse(diffObj.ToString(), out var paramDiff))
            difficulty = Math.Max(1, Math.Min(5, paramDiff));

        ExecuteSafeCommand($"bot_difficulty {difficulty}");
        Server.NextFrame(() => player.PrintToChat($" [GrokPlugin] Set bot difficulty to {difficulty}"));
        return true;
    }

    private bool ExecuteTeamNameAction(string? target, Dictionary<string, object> parameters, CCSPlayerController player)
    {
        if (string.IsNullOrWhiteSpace(target))
        {
            Server.NextFrame(() => player.PrintToChat(" [GrokPlugin] No team name specified."));
            return false;
        }

        if (parameters.TryGetValue("team", out var teamObj))
        {
            string team = teamObj.ToString()?.ToLower() ?? "";
            if (team == "1" || team == "t" || team == "terrorist")
                ExecuteSafeCommand($"mp_teamname_1 {target}");
            else if (team == "2" || team == "ct" || team == "counter")
                ExecuteSafeCommand($"mp_teamname_2 {target}");
            else
                ExecuteSafeCommand($"mp_teamname_1 {target}"); // Default to T
        }
        else
        {
            ExecuteSafeCommand($"mp_teamname_1 {target}"); // Default to T
        }

        // Send dramatic confirmation message to all players
        Server.NextFrame(() => 
        {
            Server.PrintToChatAll($" [GrokPlugin] Grok has renamed the team to: '{target}' - deal with it! 💀");
            player.PrintToChat($" [GrokPlugin] Set team name to: {target}");
        });
        return true;
    }

    private bool ExecuteSayAction(string? message, CCSPlayerController player)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            Server.NextFrame(() => player.PrintToChat(" [GrokPlugin] No message specified for say action."));
            return false;
        }

        Server.PrintToChatAll($" Grok: {message}");
        return true;
    }

    private bool ExecuteRestartAction(CCSPlayerController player)
    {
        ExecuteSafeCommand("mp_restartgame");
        
        // Send dramatic confirmation message to all players
        Server.NextFrame(() => 
        {
            Server.PrintToChatAll($" [GrokPlugin] Grok has initiated a server restart! Brace yourselves for chaos! 🌪️");
            player.PrintToChat($" [GrokPlugin] Restarted the game");
        });
        return true;
    }

    private bool ExecuteSwitchTeamAction(string? target, CCSPlayerController player)
    {
        if (string.IsNullOrWhiteSpace(target))
        {
            Server.NextFrame(() => player.PrintToChat(" [GrokPlugin] No team specified for switch action."));
            return false;
        }

        var resolved = ResolvePlayerName(target);
        if (string.IsNullOrWhiteSpace(resolved))
        {
            Server.NextFrame(() => player.PrintToChat(" [GrokPlugin] Could not resolve player name."));
            return false;
        }

        ExecuteSafeCommand($"mp_switch_team {resolved}");
        
        // Send dramatic confirmation message to all players
        Server.NextFrame(() => 
        {
            Server.PrintToChatAll($" [GrokPlugin] Grok has banished {resolved} to the opposite team! Good luck over there! 😈");
            player.PrintToChat($" [GrokPlugin] Switched {resolved} to opposite team");
        });
        return true;
    }

    private bool ExecuteGenericCommand(string action, string? target, Dictionary<string, object> parameters, CCSPlayerController player)
    {
        // Build command string
        var command = action;
        if (!string.IsNullOrWhiteSpace(target))
            command += $" {target}";
        
        foreach (var param in parameters)
        {
            command += $" {param.Value}";
        }

        ExecuteSafeCommand(command);
        
        // Send confirmation message to all players
        Server.NextFrame(() => 
        {
            Server.PrintToChatAll($" [GrokPlugin] Grok has executed: {command} - the server trembles! ⚡");
            player.PrintToChat($" [GrokPlugin] Executed: {command}");
        });
        return true;
    }

    private bool ExecuteWebSearchAction(string? query, Dictionary<string, object> parameters, CCSPlayerController player)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            Server.NextFrame(() => player.PrintToChat(" [GrokPlugin] No search query provided."));
            return false;
        }

        string steamId = player.SteamID.ToString();
        
        // Check rate limiting
        if (_webSearchRateLimit.TryGetValue(steamId, out var lastSearch))
        {
            if ((DateTime.Now - lastSearch).TotalMinutes < WEB_SEARCH_RATE_LIMIT_MINUTES)
            {
                var remainingMinutes = WEB_SEARCH_RATE_LIMIT_MINUTES - (DateTime.Now - lastSearch).TotalMinutes;
                Server.NextFrame(() => player.PrintToChat($" [GrokPlugin] Web search rate limited. Try again in {remainingMinutes:F1} minutes."));
                return false;
            }
        }

        // Check if we have cached results
        string cacheKey = query.ToLowerInvariant().Trim();
        if (_webSearchCache.TryGetValue(cacheKey, out var cached) && cached.ExpiresAt > DateTime.Now)
        {
            Logger.LogInformation($"[GrokPlugin] Using cached web search results for: {query}");
            DisplayWebSearchResults(cached.Results, player, query);
            return true;
        }

        // Update rate limit
        _webSearchRateLimit[steamId] = DateTime.Now;

        // Perform web search asynchronously
        _ = PerformWebSearchAsync(query, player, cacheKey);
        
        Server.NextFrame(() => player.PrintToChat($" [GrokPlugin] Searching the web for: {Truncate(query, 50)}"));
        return true;
    }

    private async Task PerformWebSearchAsync(string query, CCSPlayerController player, string cacheKey)
    {
        try
        {
            // Use a simple, safe search approach with DuckDuckGo Instant Answer API
            var results = await SearchDuckDuckGoAsync(query);
            
            if (results.Any())
            {
                // Cache the results
                _webSearchCache[cacheKey] = new WebSearchCache
                {
                    Results = results,
                    ExpiresAt = DateTime.Now.AddMinutes(30)
                };

                // Display results
                Server.NextFrame(() => DisplayWebSearchResults(results, player, query));
            }
            else
            {
                Server.NextFrame(() => player.PrintToChat(" [GrokPlugin] No search results found."));
            }
        }
        catch (Exception ex)
        {
            Logger.LogError($"[GrokPlugin] Web search error: {ex.Message}");
            Server.NextFrame(() => player.PrintToChat(" [GrokPlugin] Web search failed. Try again later."));
        }
    }

    private async Task<List<WebSearchResult>> SearchDuckDuckGoAsync(string query)
    {
        var results = new List<WebSearchResult>();
        
        try
        {
            // Use DuckDuckGo Instant Answer API (no API key required, rate-limited but safe)
            var url = $"https://api.duckduckgo.com/?q={Uri.EscapeDataString(query)}&format=json&no_html=1&skip_disambig=1";
            
            using var response = await client.GetAsync(url);
            if (response.IsSuccessStatusCode)
            {
                var json = await response.Content.ReadAsStringAsync();
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                // Extract instant answer
                if (root.TryGetProperty("Abstract", out var abstractEl) && !string.IsNullOrWhiteSpace(abstractEl.GetString()))
                {
                    results.Add(new WebSearchResult
                    {
                        Title = root.TryGetProperty("AbstractText", out var titleEl) ? titleEl.GetString() ?? "Search Result" : "Search Result",
                        Snippet = abstractEl.GetString() ?? "",
                        Url = root.TryGetProperty("AbstractURL", out var urlEl) ? urlEl.GetString() ?? "" : ""
                    });
                }

                // Extract related topics (limited to prevent spam)
                if (root.TryGetProperty("RelatedTopics", out var topicsEl) && topicsEl.ValueKind == JsonValueKind.Array)
                {
                    int count = 0;
                    foreach (var topic in topicsEl.EnumerateArray())
                    {
                        if (count >= MAX_WEB_SEARCH_RESULTS - 1) break; // Leave room for instant answer
                        
                        if (topic.TryGetProperty("Text", out var textEl) && !string.IsNullOrWhiteSpace(textEl.GetString()))
                        {
                            results.Add(new WebSearchResult
                            {
                                Title = "Related Topic",
                                Snippet = textEl.GetString() ?? "",
                                Url = ""
                            });
                            count++;
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Logger.LogError($"[GrokPlugin] DuckDuckGo search error: {ex.Message}");
        }

        return results;
    }

    private void DisplayWebSearchResults(List<WebSearchResult> results, CCSPlayerController player, string query)
    {
        if (!results.Any())
        {
            player.PrintToChat(" [GrokPlugin] No search results found.");
            return;
        }

        // Display search query with chunking
        var queryText = $"Web search results for: {Truncate(query, 40)}";
        var queryChunks = ChunkTextForChat(queryText, 127, " [GrokPlugin] ");
        foreach (var chunk in queryChunks)
        {
            player.PrintToChat(chunk);
        }

        // Display results with chunking
        for (int i = 0; i < Math.Min(results.Count, MAX_WEB_SEARCH_RESULTS); i++)
        {
            var result = results[i];
            var resultText = $"{i + 1}. {result.Snippet}";
            
            // Chunk the result text
            var resultChunks = ChunkTextForChat(resultText, 127, " [GrokPlugin] ");
            foreach (var chunk in resultChunks)
            {
                player.PrintToChat(chunk);
            }
            
            // Display URL if available (with chunking)
            if (!string.IsNullOrWhiteSpace(result.Url))
            {
                var urlText = $"URL: {result.Url}";
                var urlChunks = ChunkTextForChat(urlText, 127, " [GrokPlugin]    ");
                foreach (var chunk in urlChunks)
                {
                    player.PrintToChat(chunk);
                }
            }
        }

        // Add timestamp
        var timestamp = results.First().Timestamp.ToString("HH:mm");
        var timestampText = $"Results from {timestamp} (cached for 30 min)";
        var timestampChunks = ChunkTextForChat(timestampText, 127, " [GrokPlugin] ");
        foreach (var chunk in timestampChunks)
        {
            player.PrintToChat(chunk);
        }
    }

    private string GetRoundContextAsString()
    {
        var contextBuilder = new System.Text.StringBuilder();
        contextBuilder.AppendLine("--- Current Round Context ---");

        // Always include basic round info
        contextBuilder.AppendLine($"Round Status: Active");
        contextBuilder.AppendLine($"Players Online: {Utilities.GetPlayers().Count(p => p?.IsValid == true && !p.IsBot)}");
        
        // Add events if any exist
        if (_roundEventLog.Any())
        {
            contextBuilder.AppendLine("\nRecent Events:");
            _roundEventLog.TakeLast(5).ToList().ForEach(log => contextBuilder.AppendLine($"- {log}"));
        }
        else
        {
            contextBuilder.AppendLine("\nEvents: Round just started, no events yet.");
        }

        // Add player stats if any exist
        contextBuilder.AppendLine("\nPlayer Performance (K/D/HS):");
        if (_playerRoundStats.Any())
        {
            var players = Utilities.GetPlayers();
            foreach (var entry in _playerRoundStats.TakeLast(8)) // Limit to 8 most active players
            {
                var player = players.FirstOrDefault(p => p?.SteamID.ToString() == entry.Key);
                var playerName = player?.PlayerName ?? $"Player ({entry.Key})";
                var stats = entry.Value;
                contextBuilder.AppendLine($"- {playerName}: {stats.Kills}K / {stats.Deaths}D / {stats.Headshots}HS");
            }
        }
        else
        {
            contextBuilder.AppendLine("No kills recorded yet this round.");
        }

        // Add server info
        contextBuilder.AppendLine("\nServer Info:");
        contextBuilder.AppendLine("- AWP-only server");
        contextBuilder.AppendLine("- Bot difficulty: 3");
        contextBuilder.AppendLine("- Round in progress");

        contextBuilder.AppendLine("--------------------------");
        return contextBuilder.ToString();
    }

    private string GetPlayerMemoryContextAsString()
    {
        var memoryBuilder = new System.Text.StringBuilder();
        memoryBuilder.AppendLine("--- Player Memories & Context ---");

        var players = Utilities.GetPlayers().Where(p => p?.IsValid == true && !p.IsBot).ToList();
        
        if (players.Any())
        {
            memoryBuilder.AppendLine($"Active Players ({players.Count}):");
            
            foreach (var player in players)
            {
                string steamId = player.SteamID.ToString();
                memoryBuilder.AppendLine($"\n{player.PlayerName}:");
                
                // Add current round stats
                if (_playerRoundStats.TryGetValue(steamId, out var stats))
                {
                    memoryBuilder.AppendLine($"  Current Round: {stats.Kills}K / {stats.Deaths}D / {stats.Headshots}HS");
                }
                else
                {
                    memoryBuilder.AppendLine($"  Current Round: No activity yet");
                }
                
                // Add notable memories
                if (_playerMemories.TryGetValue(steamId, out var memory) && memory?.NotableEvents?.Any() == true)
                {
                    memoryBuilder.AppendLine($"  Notable Events:");
                    memory.NotableEvents.TakeLast(3).ToList().ForEach(e => memoryBuilder.AppendLine($"    - {e}"));
                }
                else
                {
                    memoryBuilder.AppendLine($"  Notable Events: New player, no history yet");
                }
            }
        }
        else
        {
            memoryBuilder.AppendLine("No human players currently online.");
        }

        memoryBuilder.AppendLine("-----------------------");
        return memoryBuilder.ToString();
    }

    private string GetSystemPrompt()
    {
        if (_config == null) return string.Empty;
        
        return _config.SystemPrompt
            .Replace("{{allowed_commands}}", string.Join(", ", _config.AllowedCommands))
            .Replace("{{mode}}", _config.Mode);
    }

    private async Task HandleProactiveLLMRequest(string eventDescription)
    {
        try
        {
            if (string.IsNullOrEmpty(_config?.ApiUrl) || string.IsNullOrEmpty(_config?.ApiKey))
            {
                Logger.LogWarning("[GrokPlugin] Proactive chat triggered, but API is not configured.");
                return;
            }

            var messages = new List<AIMessage>
            {
                new AIMessage { role = "system", content = GetSystemPrompt() },
                new AIMessage { role = "system", content = GetRoundContextAsString() },
                new AIMessage { role = "system", content = GetPlayerMemoryContextAsString() },
                new AIMessage { role = "user", content = $"SYSTEM EVENT: {eventDescription}. Please provide a brief, in-character comment about this." }
            };

            // Route to Replicate predictions if configured, else fall back to OpenAI-compatible endpoint
            if ((_config.ApiUrl?.Contains("/v1/predictions") ?? false) && !string.IsNullOrWhiteSpace(_config.Model))
            {
                var replicateText = await SendReplicatePredictionAsync(messages);
                if (!string.IsNullOrEmpty(replicateText))
                {
                    Server.NextFrame(() =>
                    {
                        Server.PrintToChatAll($" Grok: {replicateText}");
                    });
                }
                return;
            }

            if (_config == null)
            {
                Logger.LogError("Configuration is not loaded");
                return;
            }

            if (string.IsNullOrEmpty(_config.Model))
            {
                Logger.LogError("Model is not configured in the configuration");
                return;
            }

            var request = new ChatRequest
            {
                model = _config.Model,
                messages = messages
            };

            var json = JsonSerializer.Serialize(request);
            var content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");
            Logger.LogInformation($"[GrokPlugin] Sending chat request → url={_config.ApiUrl}, model={_config.Model}, messages={messages.Count}");
            var response = await PostWithFallbacksAsync(_config.ApiUrl!, _config.Model, content);
            var result = await response.Content.ReadAsStringAsync();

            if (response.IsSuccessStatusCode)
            {
                try
                {
                    using JsonDocument doc = JsonDocument.Parse(result);
                    var choices = doc.RootElement.GetProperty("choices");
                    if (choices.GetArrayLength() > 0)
                    {
                        var message = choices[0].GetProperty("message").GetProperty("content").GetString() ?? "";
                        if (!string.IsNullOrEmpty(message))
                        {
                            var chunks = ChunkTextForChat(message, 127, " Grok: ");
                            Server.NextFrame(() =>
                            {
                                foreach (var chunk in chunks)
                                {
                                    Server.PrintToChatAll(chunk);
                                }
                            });
                        }
                        else
                        {
                            Logger.LogWarning("[GrokPlugin] Empty message content in successful response.");
                            Logger.LogDebug($"[GrokPlugin] Raw response: {Truncate(result, 2000)}");
                        }
                    }
                    else
                    {
                        Logger.LogWarning("[GrokPlugin] No choices returned by provider.");
                        Logger.LogDebug($"[GrokPlugin] Raw response: {Truncate(result, 2000)}");
                    }
                }
                catch (Exception ex)
                {
                    Logger.LogError($"[GrokPlugin] Failed to parse success response: {ex.Message}");
                    Logger.LogDebug($"[GrokPlugin] Raw response: {Truncate(result, 2000)}");
                }
            }
            else
            {
                Logger.LogError($"[GrokPlugin] API Error: {(int)response.StatusCode} {response.StatusCode}");
                Logger.LogError($"[GrokPlugin] Response body: {Truncate(result, 2000)}");
            }
        }
        catch (Exception e)
        {
            Logger.LogError($"[GrokPlugin] Error processing proactive LLM request: {e.Message}");
        }
    }

    private async Task HandleLLMRequest(CCSPlayerController player, string prompt, bool isCommandRequest = false)
    {
        try
        {
            if (string.IsNullOrEmpty(_config?.ApiUrl) || string.IsNullOrEmpty(_config?.ApiKey))
            {
                Server.NextFrame(() => player.PrintToChat(" [GrokPlugin] API not configured. Check config.json"));
                return;
            }

            var messages = new List<AIMessage>();
            
            // Add system prompt
            messages.Add(new AIMessage 
            { 
                role = "system",
                content = GetSystemPrompt()
            });

            // Refresh context data to ensure it's current
            RefreshContextData();
            
            // ALWAYS add round context (essential for every response)
            var roundContext = GetRoundContextAsString();
            if (!string.IsNullOrWhiteSpace(roundContext))
            {
                messages.Add(new AIMessage
                {
                    role = "system",
                    content = roundContext
                });
                Logger.LogDebug($"[GrokPlugin] Added round context: {Truncate(roundContext, 100)}");
            }
            else
            {
                Logger.LogWarning("[GrokPlugin] No round context available!");
            }

            // ALWAYS add player memory context (personalized responses)
            var memoryContext = GetPlayerMemoryContextAsString();
            if (!string.IsNullOrWhiteSpace(memoryContext))
            {
                messages.Add(new AIMessage
                {
                    role = "system",
                    content = memoryContext
                });
                Logger.LogDebug($"[GrokPlugin] Added memory context: {Truncate(memoryContext, 100)}");
            }
            else
            {
                Logger.LogWarning("[GrokPlugin] No memory context available!");
            }

            // Add context for admin commands
            if (isCommandRequest)
            {
                messages.Add(new AIMessage 
                { 
                    role = "system",
                    content = "ADMIN MODE: You have access to server tools. When you need to use tools, respond with a JSON object. You can chain multiple actions together and provide context for your actions.\n\n" +
                    "Tool Schema: {\"action\":\"<tool_name>\",\"target\":\"<optional_target>\",\"reason\":\"<why_you're_doing_this>\",\"chain\":[\"<additional_actions>\",\"<to_execute>\",\"<in_sequence>\"],\"parameters\":{\"<param_name>\":\"<value>\"}}\n\n" +
                    "Available Tools:\n" +
                    "- kill: Kill a player (requires target)\n" +
                    "- bot_add: Add bots (can specify team T/CT, count in parameters)\n" +
                    "- bot_kick: Remove all bots\n" +
                    "- bot_difficulty: Set bot difficulty 1-5\n" +
                    "- team_name: Set team name (specify team in parameters)\n" +
                    "- say: Send a message to all players\n" +
                    "- restart: Restart the game\n" +
                    "- switch_team: Switch a player's team\n" +
                    "- web_search: Search the internet (query in target, rate limited)\n\n" +
                    "Examples:\n" +
                    "- Simple: {\"action\":\"kill\",\"target\":\"playerName\",\"reason\":\"You talked back to me\"}\n" +
                    "- Chained: {\"action\":\"bot_add\",\"target\":\"T\",\"reason\":\"Time for some chaos\",\"chain\":[\"bot_difficulty\",\"team_name\"],\"parameters\":{\"count\":\"8\",\"level\":\"3\",\"team\":\"1\"}}\n" +
                    "- Web Search: {\"action\":\"web_search\",\"target\":\"CS2 AWP strategies\",\"reason\":\"Looking up AWP tips for the player\"}\n" +
                    "Respond ONLY with the JSON tool call, no other text."
                });
            }
            else
            {
                            // Add context-aware tool usage guidance for regular chat
            messages.Add(new AIMessage
            {
                role = "system",
                content = "CHAT MODE: You can chat normally with players. However, if a player requests an action that requires server tools (like killing someone, adding bots, etc.), you can respond with a tool call JSON object.\n\n" +
                "Use tools when players:\n" +
                "- Ask you to kill them or someone else\n" +
                "- Request bots to be added/removed\n" +
                "- Want team names changed\n" +
                "- Ask for server restarts\n" +
                "- Request team switches\n" +
                "- Ask for information you don't know (use web_search)\n" +
                "- Want to learn about CS2 strategies, weapons, or game mechanics\n\n" +
                "For regular conversation, just chat normally. For tool requests, use the JSON format:\n" +
                "{\"action\":\"<tool_name>\",\"target\":\"<target>\",\"reason\":\"<explanation>\",\"chain\":[\"<chained_actions>\"],\"parameters\":{\"<params>\"}}\n\n" +
                "IMPORTANT: Control your response length intelligently. CS2 chat has a 127 character limit, so:\n" +
                "- Keep responses concise and complete\n" +
                "- End naturally at sentence boundaries\n" +
                "- If you need to say more, use multiple short responses\n" +
                "- Don't start thoughts you can't finish\n" +
                "- Use the chunking system strategically\n\n" +
                "Web search is rate-limited to 1 search per 2 minutes per player and results are cached for 30 minutes."
            });
            }

            messages.Add(new AIMessage { role = "user", content = prompt });

            // Replicate predictions path
            if ((_config?.ApiUrl?.Contains("/v1/predictions") ?? false))
            {
                if (string.IsNullOrWhiteSpace(_config?.Model))
                {
                    Logger.LogWarning("[GrokPlugin] Replicate predictions selected but model version is empty. Set GROK_MODEL to the version hash.");
                    Server.NextFrame(() => player.PrintToChat(" [GrokPlugin] Missing model version. Set GROK_MODEL to the llama-2-7b-chat version hash."));
                    return;
                }
                Logger.LogInformation($"[GrokPlugin] Replicate path → model={_config?.Model}");
                // Single-flight guard
                lock (_llmLock)
                {
                    if (_llmInFlight)
                    {
                        Server.NextFrame(() => player.PrintToChat(" [GrokPlugin] Busy with another request, try again in a moment."));
                        return;
                    }
                    _llmInFlight = true;
                }
                // Thinking feedback after 8s if no answer yet
                var thinkingCts = new CancellationTokenSource();
                var thinking = Task.Run(async () => {
                    try { await Task.Delay(8000, thinkingCts.Token); } catch {}
                    if (!thinkingCts.IsCancellationRequested)
                    {
                        Server.NextFrame(() => player.PrintToChat(" [GrokPlugin] Grok is thinking..."));
                    }
                });
                var replicateTask = SendReplicatePredictionAsync(messages);
                var replicateText = await replicateTask;
                thinkingCts.Cancel();
                lock (_llmLock) { _llmInFlight = false; }
                // Check if this is a tool call (even in chat mode)
                if (TryParseToolCall(replicateText, out var toolCall))
                {
                    Logger.LogInformation($"[GrokPlugin] Replicate LLM chose to use tools in chat mode: {toolCall.Action}");
                    ExecuteStructuredToolCall(replicateText, player);
                }
                else if (isCommandRequest)
                {
                    // Admin mode - expect tool calls
                    if (!string.IsNullOrEmpty(replicateText))
                    {
                        ExecuteStructuredToolCall(replicateText, player);
                    }
                    else
                    {
                        Server.NextFrame(() => player.PrintToChat(" [GrokPlugin] AI returned no command."));
                    }
                }
                else
                {
                    // Regular chat response
                    if (!string.IsNullOrWhiteSpace(replicateText))
                    {
                        var text = replicateText;
                        if (_config?.MaxChatCharacters > 0 && text.Length > _config.MaxChatCharacters)
                        {
                            // Try to end on a sentence boundary before truncation
                            int cut = _config.MaxChatCharacters;
                            int lastPeriod = text.LastIndexOf('.', Math.Min(text.Length - 1, cut));
                            if (lastPeriod >= 40) cut = lastPeriod + 1;
                            text = text.Substring(0, cut).TrimEnd();
                        }
                        var finalText = text;
                        var chunks = ChunkTextForChat(finalText, 127, " Grok: ");
                        if (chunks != null)
                        {
                            Server.NextFrame(() =>
                            {
                                foreach (var chunk in chunks)
                                {
                                    Server.PrintToChatAll(chunk);
                                }
                            });
                        }
                    }
                    else
                    {
                        Server.NextFrame(() => player.PrintToChat(" [GrokPlugin] The AI returned no text."));
                    }
                }
                return;
            }

            if (_config == null)
            {
                Logger.LogError("Configuration is not loaded");
                return;
            }

            if (string.IsNullOrEmpty(_config.Model))
            {
                Logger.LogError("Model is not configured in the configuration");
                return;
            }

            var request = new ChatRequest
            {
                model = _config.Model,
                messages = messages
            };

            var json = JsonSerializer.Serialize(request);
            var content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");
            Logger.LogInformation($"[GrokPlugin] Sending user request → url={_config?.ApiUrl}, model={_config?.Model}, isCommand={isCommandRequest}");
            Logger.LogDebug($"[GrokPlugin] Request body: {Truncate(json, 1500)}");
            var response = await PostWithFallbacksAsync(_config?.ApiUrl ?? string.Empty, _config?.Model ?? string.Empty, content);
            var result = await response.Content.ReadAsStringAsync();
            
            Logger.LogInformation($"[GrokPlugin] Response status: {(int)response.StatusCode} {response.StatusCode}");
            Logger.LogInformation($"[GrokPlugin] Response body length: {result.Length}");
            if (!response.IsSuccessStatusCode)
            {
                Logger.LogWarning($"[GrokPlugin] Response headers: {string.Join(", ", response.Headers.Select(h => $"{h.Key}={string.Join(";", h.Value)}"))}");
            }

            if (response.IsSuccessStatusCode)
            {
                try
                {
                    using JsonDocument doc = JsonDocument.Parse(result);
                    var choices = doc.RootElement.GetProperty("choices");
                    if (choices.GetArrayLength() > 0)
                    {
                                            var message = choices[0].GetProperty("message").GetProperty("content").GetString() ?? "";
                        
                        // Check if this is a tool call (even in chat mode)
                        if (TryParseToolCall(message, out var toolCall))
                        {
                            Logger.LogInformation($"[GrokPlugin] LLM chose to use tools in chat mode: {toolCall.Action}");
                            ExecuteStructuredToolCall(message, player);
                        }
                        else if (isCommandRequest)
                        {
                            // Admin mode - expect tool calls
                            if (!string.IsNullOrEmpty(message))
                            {
                                ExecuteStructuredToolCall(message, player);
                            }
                        }
                        else
                        {
                            // Regular chat response with intelligent chunking
                            var chunks = ChunkTextForChat(message, 127, " Grok: ");
                            
                            // Check if the AI wants to continue
                            if (WantsToContinue(message))
                            {
                                // Add a continuation indicator to the last chunk
                                if (chunks.Any())
                                {
                                    var lastChunk = chunks.Last();
                                    
                                    // If AI wants to continue, suggest they ask for more
                                    if (WantsToContinue(message))
                                    {
                                        Server.NextFrame(() =>
                                        {
                                            Server.PrintToChatAll(" Grok: Ask me to continue if you want more...");
                                        });
                                    }
                                }
                            }
                        }
                    }
                    else
                    {
                        Logger.LogWarning("[GrokPlugin] No choices returned by provider.");
                        Logger.LogDebug($"[GrokPlugin] Raw response: {Truncate(result, 2000)}");
                        Server.NextFrame(() => player.PrintToChat(" [GrokPlugin] The AI returned no choices."));
                    }
                }
                catch (Exception ex)
                {
                    Logger.LogError($"[GrokPlugin] Parse error: {ex.Message}");
                    Logger.LogDebug($"[GrokPlugin] Raw response: {Truncate(result, 2000)}");
                    Server.NextFrame(() => player.PrintToChat(" [GrokPlugin] The AI response was unreadable."));
                }
            }
            else
            {
                Logger.LogError($"[GrokPlugin] API Error: {(int)response.StatusCode} {response.StatusCode}");
                Logger.LogError($"[GrokPlugin] Response body: {Truncate(result, 2000)}");
                Server.NextFrame(() => player.PrintToChat(" [GrokPlugin] The AI is having trouble thinking right now."));
            }
        }
        catch (Exception e)
        {
            Logger.LogError($"Error processing LLM request: {e.Message}");
            Server.NextFrame(() => player.PrintToChat(" [GrokPlugin] Sorry, there was an error processing your request."));
        }
    }

    private async Task<HttpResponseMessage> PostWithFallbacksAsync(string apiUrl, string configuredModel, HttpContent originalContent)
    {
        // Prepare model variants and auth schemes to maximize compatibility across OpenAI-compatible providers
        var modelCandidates = new List<string>();
        if (!string.IsNullOrWhiteSpace(configuredModel)) modelCandidates.Add(configuredModel);
        // Common fallback for Replicate DeepSeek
        if (!modelCandidates.Contains("deepseek-v3")) modelCandidates.Add("deepseek-v3");

        var authSchemes = new[] { "Bearer", "Token" };

        foreach (var model in modelCandidates)
        {
            // Rebuild content each attempt because HttpContent can be consumed
            string body = await originalContent.ReadAsStringAsync();
            try
            {
                using var doc = JsonDocument.Parse(body);
                var root = doc.RootElement.Clone();
                var dict = new Dictionary<string, object?>();
                foreach (var p in root.EnumerateObject()) dict[p.Name] = p.Value.Deserialize<object?>();
                dict["model"] = model;
                var refreshedJson = JsonSerializer.Serialize(dict);
                using var refreshedContent = new StringContent(refreshedJson, System.Text.Encoding.UTF8, "application/json");

                foreach (var scheme in authSchemes)
                {
                    // Clear any existing headers and set new ones
                    client.DefaultRequestHeaders.Clear();
                    client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue(scheme, _config?.ApiKey ?? string.Empty);
                    Logger.LogDebug($"[GrokPlugin] Trying auth scheme: {scheme}");
                    var resp = await client.PostAsync(apiUrl, refreshedContent);
                    Logger.LogDebug($"[GrokPlugin] Auth scheme {scheme} result: {(int)resp.StatusCode} {resp.StatusCode}");
                    if (resp.IsSuccessStatusCode) return resp;
                }
            }
            catch (Exception ex)
            {
                Logger.LogError($"[GrokPlugin] Request build error: {ex.Message}");
            }
        }

        // Final attempt with original content + Bearer (default)
        client.DefaultRequestHeaders.Clear();
        client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _config?.ApiKey ?? string.Empty);
        Logger.LogDebug("[GrokPlugin] Final attempt with Bearer auth");
        var finalResp = await client.PostAsync(apiUrl, originalContent);
        Logger.LogDebug($"[GrokPlugin] Final attempt result: {(int)finalResp.StatusCode} {finalResp.StatusCode}");
        return finalResp;
    }

    private async Task<string> SendReplicatePredictionAsync(List<AIMessage> messages)
    {
        // Expect _config.Model to be a VERSION hash string for the model (llama-2-7b-chat)
        try
        {
            using var overallCts = new CancellationTokenSource(TimeSpan.FromSeconds(90));
            // Build prompt by concatenating messages. Llama 2 prefers [INST]...[/INST] format
            var sb = new System.Text.StringBuilder();
            string? system = messages.FirstOrDefault(m => m.role == "system")?.content;
            if (!string.IsNullOrWhiteSpace(system))
            {
                sb.Append("<<SYS>>\n").Append(system).Append("\n<</SYS>>\n\n");
            }
            foreach (var m in messages.Where(m => m.role != "system"))
            {
                if (m.role == "user")
                {
                    sb.Append("[INST] ").Append(m.content).Append(" [/INST]\n");
                }
                else if (m.role == "assistant")
                {
                    sb.Append(m.content).Append("\n");
                }
            }

            var payload = new
            {
                version = _config?.Model,
                input = new
                {
                    prompt = sb.ToString(),
                    system_prompt = system ?? string.Empty,
                    max_new_tokens = 180,
                    temperature = 0.5,
                    top_p = 0.9
                }
            };

            var json = JsonSerializer.Serialize(payload);
            using var content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");
            client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Token", _config?.ApiKey ?? string.Empty);
            Logger.LogInformation($"[GrokPlugin] Replicate create → url={_config?.ApiUrl}, version={_config?.Model}");
            var createResp = await client.PostAsync(_config?.ApiUrl, content, overallCts.Token);
            var createBody = await createResp.Content.ReadAsStringAsync();
            if (!createResp.IsSuccessStatusCode)
            {
                Logger.LogError($"[GrokPlugin] Replicate create error: {(int)createResp.StatusCode} {createResp.StatusCode}\n{Truncate(createBody, 1000)}");
                return string.Empty;
            }

            using var doc = JsonDocument.Parse(createBody);
            var id = doc.RootElement.TryGetProperty("id", out var idEl) ? idEl.GetString() : null;
            var status = doc.RootElement.TryGetProperty("status", out var stEl) ? stEl.GetString() : null;
            string? outputText = ExtractReplicateOutput(doc.RootElement);
            string? getUrl = null;
            if (doc.RootElement.TryGetProperty("urls", out var urlsEl) && urlsEl.ValueKind == JsonValueKind.Object && urlsEl.TryGetProperty("get", out var getEl))
            {
                getUrl = getEl.GetString();
            }
            if (getUrl == null && !string.IsNullOrEmpty(id) && !string.IsNullOrEmpty(_config?.ApiUrl))
            {
                // Fallback: construct GET URL
                var baseUrl = _config!.ApiUrl!.TrimEnd('/');
                getUrl = baseUrl + "/" + id;
            }
            Logger.LogInformation($"[GrokPlugin] Replicate created id={id}, status={status}, getUrl={(getUrl ?? "<null>")}");

            // If finished immediately
            if (status == "succeeded" && !string.IsNullOrEmpty(outputText))
                return Truncate(outputText, 200);

            // Poll until done
            if (string.IsNullOrEmpty(getUrl))
            {
                Logger.LogError("[GrokPlugin] Replicate response missing get URL; cannot poll.");
                return string.Empty;
            }
            for (int i = 0; i < 40; i++)
            {
                await Task.Delay(2000, overallCts.Token);
                if (_config?.VerboseLogging == true) Logger.LogDebug($"[GrokPlugin] Replicate poll {i+1}/40 → {getUrl}");
                var getResp = await client.GetAsync(getUrl, overallCts.Token);
                var getBody = await getResp.Content.ReadAsStringAsync();
                if (!getResp.IsSuccessStatusCode)
                {
                    Logger.LogError($"[GrokPlugin] Replicate poll error: {(int)getResp.StatusCode} {getResp.StatusCode}\n{Truncate(getBody, 1000)}");
                    break;
                }
                using var gdoc = JsonDocument.Parse(getBody);
                var groot = gdoc.RootElement;
                var gstatus = groot.GetProperty("status").GetString();
                outputText = ExtractReplicateOutput(groot);
                if (_config?.VerboseLogging == true) Logger.LogDebug($"[GrokPlugin] Replicate status={gstatus}, textLen={(outputText?.Length ?? 0)}");
                if (gstatus == "succeeded")
                {
                    if (string.IsNullOrWhiteSpace(outputText))
                    {
                        // Fallback: try to pull any text-like content and log a small snippet
                        try
                        {
                            if (groot.TryGetProperty("output", out var outEl))
                            {
                                if (outEl.ValueKind == JsonValueKind.Array)
                                {
                                    var sbflat = new System.Text.StringBuilder();
                                    foreach (var sub in outEl.EnumerateArray())
                                    {
                                        var t = ExtractReplicateOutput(new System.Text.Json.JsonElement()); // no-op, keep signature use
                                        // simple flatten for fallback
                                        if (sub.ValueKind == JsonValueKind.String) sbflat.Append(sub.GetString());
                                        else if (sub.ValueKind == JsonValueKind.Object)
                                        {
                                            if (sub.TryGetProperty("content", out var c) && c.ValueKind == JsonValueKind.String) sbflat.Append(c.GetString());
                                            else if (sub.TryGetProperty("text", out var tt) && tt.ValueKind == JsonValueKind.String) sbflat.Append(tt.GetString());
                                        }
                                    }
                                    outputText = sbflat.ToString().Trim();
                                }
                            }
                        }
                        catch {}
                        if (string.IsNullOrWhiteSpace(outputText))
                        {
                            Logger.LogWarning($"[GrokPlugin] Replicate succeeded but no text parsed. Raw snippet: {Truncate(getBody, 300)}");
                        }
                    }
                    return Truncate(outputText ?? string.Empty, 200);
                }
                if (gstatus == "failed" || gstatus == "canceled")
                {
                    Logger.LogError($"[GrokPlugin] Replicate prediction {id} ended with status {gstatus}");
                    break;
                }
            }
        }
        catch (Exception ex)
        {
            Logger.LogError($"[GrokPlugin] Replicate predictions error: {ex.Message}");
        }
        return string.Empty;
    }

    private string? ExtractReplicateOutput(JsonElement root)
    {
        try
        {
            // Replicate outputs often in array of strings or a single string
            if (root.TryGetProperty("output", out var output))
            {
                if (output.ValueKind == JsonValueKind.Array)
                {
                    string Flatten(JsonElement el)
                    {
                        if (el.ValueKind == JsonValueKind.String) return el.GetString() ?? string.Empty;
                        if (el.ValueKind == JsonValueKind.Object)
                        {
                            if (el.TryGetProperty("content", out var c) && c.ValueKind == JsonValueKind.String) return c.GetString() ?? string.Empty;
                            if (el.TryGetProperty("text", out var t) && t.ValueKind == JsonValueKind.String) return t.GetString() ?? string.Empty;
                        }
                        if (el.ValueKind == JsonValueKind.Array)
                        {
                            var inner = new System.Text.StringBuilder();
                            foreach (var sub in el.EnumerateArray()) inner.Append(Flatten(sub));
                            return inner.ToString();
                        }
                        return string.Empty;
                    }

                    var parts = new List<string>();
                    foreach (var item in output.EnumerateArray())
                    {
                        parts.Add(Flatten(item));
                    }
                    var combined = string.Join("", parts).Trim();
                    if (!string.IsNullOrEmpty(combined)) return combined;
                }
                if (output.ValueKind == JsonValueKind.String)
                {
                    return output.GetString();
                }
            }
            // Some models might return { output_text: "..." }
            if (root.TryGetProperty("output_text", out var ot) && ot.ValueKind == JsonValueKind.String)
            {
                return ot.GetString();
            }
        }
        catch {}
        return null;
    }

    [ConsoleCommand("css_grok_toggle", "Toggle the Grok plugin")]
    public void OnToggleCommand(CCSPlayerController? player, CommandInfo command)
    {
        if (player != null && !IsAdmin(player))
        {
            player.PrintToChat(" [GrokPlugin] You must be an admin to use this command.");
            return;
        }

        if (_config != null)
        {
            _config.Enabled = !_config.Enabled;
            SaveConfig(_configPath ?? Path.Combine(ModuleDirectory, "config.json"), _config);
            string status = _config.Enabled ? "enabled" : "disabled";
            Server.PrintToChatAll($" [GrokPlugin] Plugin has been {status} by an admin.");
        }
    }

    [ConsoleCommand("css_grok_cooldown", "Set Grok cooldown seconds (admins only)")]
    public void OnSetCooldown(CCSPlayerController? player, CommandInfo command)
    {
        if (player != null && !IsAdmin(player))
        {
            player.PrintToChat(" [GrokPlugin] You must be an admin to use this command.");
            return;
        }
        if (command.ArgCount < 2)
        {
            ReplyToCommand(player, " [GrokPlugin] Usage: css_grok_cooldown <seconds>");
            return;
        }
        if (!int.TryParse(command.ArgByIndex(1), out var seconds) || seconds < 0 || seconds > 300)
        {
            ReplyToCommand(player, " [GrokPlugin] Invalid seconds. Choose 0-300.");
            return;
        }
        if (_config != null)
        {
            _config.CooldownSeconds = seconds;
            SaveConfig(Path.Combine(ModuleDirectory, "config.json"), _config);
            ReplyToCommand(player, $" [GrokPlugin] Cooldown set to {seconds}s.");
        }
    }

    [ConsoleCommand("css_grok_proactive", "Configure proactive chat: on/off chance% (admins only)")]
    public void OnSetProactive(CCSPlayerController? player, CommandInfo command)
    {
        if (player != null && !IsAdmin(player))
        {
            player.PrintToChat(" [GrokPlugin] You must be an admin to use this command.");
            return;
        }
        if (_config == null)
        {
            ReplyToCommand(player, " [GrokPlugin] Config not loaded.");
            return;
        }
        if (command.ArgCount < 2)
        {
            ReplyToCommand(player, $" [GrokPlugin] Usage: css_grok_proactive <on|off> [chance 0-100]. Current: enabled={_config.ProactiveChatEnabled}, chance={_config.ProactiveRandomChancePercent}%, cooldown={_config.ProactiveChatCooldownSeconds}s");
            return;
        }
        var onoff = command.ArgByIndex(1).ToLowerInvariant();
        if (onoff == "on" || onoff == "off")
        {
            _config.ProactiveChatEnabled = onoff == "on";
        }
        if (command.ArgCount >= 3 && int.TryParse(command.ArgByIndex(2), out var chance))
        {
            _config.ProactiveRandomChancePercent = Math.Clamp(chance, 0, 100);
        }
        SaveConfig(_configPath ?? Path.Combine(ModuleDirectory, "config.json"), _config);
        ReplyToCommand(player, $" [GrokPlugin] Proactive: enabled={_config.ProactiveChatEnabled}, chance={_config.ProactiveRandomChancePercent}%, cooldown={_config.ProactiveChatCooldownSeconds}s");
    }

    [ConsoleCommand("css_grok_status", "Show Grok config status")]    
    public void OnStatus(CCSPlayerController? player, CommandInfo command)
    {
        var apiUrl = _config?.ApiUrl ?? "<null>";
        var model = _config?.Model ?? "<null>";
        var keySet = !string.IsNullOrEmpty(_config?.ApiKey);
        var cfgPath = _configPath ?? Path.Combine(ModuleDirectory, "config.json");
        var mode = _config?.Mode ?? "chat";
        var proactive = _config?.ProactiveChatEnabled == true ? "on" : "off";
        var minimal = _config?.TestingMinimalPrompt == true ? "on" : "off";
        var msg = $"[GrokPlugin] url={apiUrl}, model={model}, key={(keySet ? "set" : "missing")}, mode={mode}, proactive={proactive}, minimalPrompt={minimal}, cfg={cfgPath}";
        ReplyToCommand(player, " " + msg);
    }

    [ConsoleCommand("css_grok_testmode", "Toggle minimal prompt test mode (admins only)")]
    public void OnTestMode(CCSPlayerController? player, CommandInfo command)
    {
        if (player != null && !IsAdmin(player))
        {
            player.PrintToChat(" [GrokPlugin] You must be an admin to use this command.");
            return;
        }
        if (_config == null) { ReplyToCommand(player, " [GrokPlugin] Config not loaded."); return; }
        _config.TestingMinimalPrompt = !_config.TestingMinimalPrompt;
        SaveConfig(_configPath ?? Path.Combine(ModuleDirectory, "config.json"), _config);
        ReplyToCommand(player, $" [GrokPlugin] Minimal prompt mode now {(_config.TestingMinimalPrompt ? "ON" : "OFF")}. Use css_grok_status to verify.");
    }

    [ConsoleCommand("css_grok_whitelist", "Add/remove a player from the whitelist")]
    public void OnWhitelistCommand(CCSPlayerController? player, CommandInfo command)
    {
        if (player != null && !IsAdmin(player))
        {
            player.PrintToChat(" [GrokPlugin] You must be an admin to use this command.");
            return;
        }

        if (command.ArgCount < 3)
        {
            ReplyToCommand(player, " [GrokPlugin] Usage: css_grok_whitelist <add/remove> <steamid>");
            return;
        }

        string action = command.ArgByIndex(1).ToLower();
        string steamId = command.ArgByIndex(2);

        if (_config != null)
        {
            if (action == "add")
            {
                if (!_config.WhitelistedSteamIds.Contains(steamId))
                {
                    _config.WhitelistedSteamIds.Add(steamId);
                    SaveConfig(_configPath ?? Path.Combine(ModuleDirectory, "config.json"), _config);
                    ReplyToCommand(player, $" [GrokPlugin] Added {steamId} to whitelist.");
                }
                else
                {
                    ReplyToCommand(player, $" [GrokPlugin] {steamId} is already on the whitelist.");
                }
            }
            else if (action == "remove")
            {
                if (_config.WhitelistedSteamIds.Remove(steamId))
                {
                    SaveConfig(_configPath ?? Path.Combine(ModuleDirectory, "config.json"), _config);
                    ReplyToCommand(player, $" [GrokPlugin] Removed {steamId} from whitelist.");
                }
                else
                {
                    ReplyToCommand(player, $" [GrokPlugin] {steamId} was not found on the whitelist.");
                }
            }
            else
            {
                ReplyToCommand(player, " [GrokPlugin] Invalid action. Use 'add' or 'remove'.");
            }
        }
    }

    [ConsoleCommand("css_grok_webcache", "Clear web search cache (admins only)")]
    public void OnWebCacheCommand(CCSPlayerController? player, CommandInfo command)
    {
        if (player != null && !IsAdmin(player))
        {
            player.PrintToChat(" [GrokPlugin] You must be an admin to use this command.");
            return;
        }

        var cacheCount = _webSearchCache.Count;
        var rateLimitCount = _webSearchRateLimit.Count;
        
        _webSearchCache.Clear();
        _webSearchRateLimit.Clear();
        
        ReplyToCommand(player, $" [GrokPlugin] Cleared web search cache: {cacheCount} cached results, {rateLimitCount} rate limit entries removed.");
        Logger.LogInformation($"[GrokPlugin] Admin cleared web search cache: {cacheCount} cached results, {rateLimitCount} rate limit entries");
    }

    [ConsoleCommand("css_grok_testchunk", "Test the chunking system with a long message (admins only)")]
    public void OnTestChunkCommand(CCSPlayerController? player, CommandInfo command)
    {
        if (player != null && !IsAdmin(player))
        {
            player.PrintToChat(" [GrokPlugin] You must be an admin to use this command.");
            return;
        }

        var testMessage = "This is a very long test message that will demonstrate the chunking system. It contains multiple sentences and should be automatically broken down into multiple chat messages that fit within the 127 character limit. The system will try to break at sentence boundaries when possible, and will add chunk indicators like [1/3], [2/3], etc. This ensures that long responses from the AI can be properly displayed in CS2 chat without being cut off abruptly.";
        
        var chunks = ChunkTextForChat(testMessage, 127, " [GrokPlugin] Test: ");
        
        ReplyToCommand(player, $" [GrokPlugin] Testing chunking system with {chunks.Count} chunks:");
        foreach (var chunk in chunks)
        {
            ReplyToCommand(player, chunk);
        }
    }

    [ConsoleCommand("css_grok_testcontinue", "Test the continuation detection system (admins only)")]
    public void OnTestContinueCommand(CCSPlayerController? player, CommandInfo command)
    {
        if (player != null && !IsAdmin(player))
        {
            player.PrintToChat(" [GrokPlugin] You must be an admin to use this command.");
            return;
        }

        var testMessages = new[]
        {
            "I have a lot more to say about this topic...",
            "The story continues with more details and examples, including",
            "There are several reasons for this, such as",
            "Let me explain further about the AWP strategies",
            "This is a complete thought that ends properly.",
            "I could go on and on about this, but",
            "The answer involves multiple factors, including",
            "Here's what you need to know about CS2 maps, especially"
        };

        ReplyToCommand(player, " [GrokPlugin] Testing continuation detection:");
        foreach (var message in testMessages)
        {
            var wantsToContinue = WantsToContinue(message);
            var isIncomplete = IsIncompleteThought(message);
            var completed = TryToCompleteThought(message);
            
            ReplyToCommand(player, $" [GrokPlugin] '{message}'");
            ReplyToCommand(player, $" [GrokPlugin]   Wants to continue: {wantsToContinue}");
            ReplyToCommand(player, $" [GrokPlugin]   Is incomplete: {isIncomplete}");
            if (completed != message)
            {
                ReplyToCommand(player, $" [GrokPlugin]   Completed: '{completed}'");
            }
            ReplyToCommand(player, "");
        }
    }

    [ConsoleCommand("css_grok_testcontext", "Test the context generation system (admins only)")]
    public void OnTestContextCommand(CCSPlayerController? player, CommandInfo command)
    {
        if (player != null && !IsAdmin(player))
        {
            player.PrintToChat(" [GrokPlugin] You must be an admin to use this command.");
            return;
        }

        // Refresh context first
        RefreshContextData();
        
        ReplyToCommand(player, " [GrokPlugin] Testing context generation:");
        
        var roundContext = GetRoundContextAsString();
        ReplyToCommand(player, $" [GrokPlugin] Round Context Length: {roundContext.Length} chars");
        ReplyToCommand(player, $" [GrokPlugin] Round Context Preview: {Truncate(roundContext, 200)}");
        
        var memoryContext = GetPlayerMemoryContextAsString();
        ReplyToCommand(player, $" [GrokPlugin] Memory Context Length: {memoryContext.Length} chars");
        ReplyToCommand(player, $" [GrokPlugin] Memory Context Preview: {Truncate(memoryContext, 200)}");
        
        // Test with a sample player
        if (player != null)
        {
            var steamId = player.SteamID.ToString();
            ReplyToCommand(player, $" [GrokPlugin] Your SteamID: {steamId}");
            ReplyToCommand(player, $" [GrokPlugin] Has Round Stats: {_playerRoundStats.ContainsKey(steamId)}");
            ReplyToCommand(player, $" [GrokPlugin] Has Memory: {_playerMemories.ContainsKey(steamId)}");
        }
    }

    [ConsoleCommand("css_grok_testbots", "Test bot spawning system (admins only)")]
    public void OnTestBotsCommand(CCSPlayerController? player, CommandInfo command)
    {
        if (player != null && !IsAdmin(player))
        {
            player.PrintToChat(" [GrokPlugin] You must be an admin to use this command.");
            return;
        }

        ReplyToCommand(player, " [GrokPlugin] Testing bot spawning system:");
        
        // Get current bot count
        var currentBots = Utilities.GetPlayers().Count(p => p?.IsBot == true);
        ReplyToCommand(player, $" [GrokPlugin] Current bots on server: {currentBots}");
        
        // Test adding 3 bots to T team
        ReplyToCommand(player, " [GrokPlugin] Adding 3 bots to T team...");
        
        // Set difficulty first
        ExecuteSafeCommand("bot_difficulty 3");
        
        // Add bots with frame delays
        ExecuteSafeCommand("bot_add T");
        Server.NextFrame(() => ExecuteSafeCommand("bot_add T"));
        Server.NextFrame(() => Server.NextFrame(() => ExecuteSafeCommand("bot_add T")));
        
        // Verify after a few frames
        Server.NextFrame(() => 
        {
            Server.NextFrame(() => 
            {
                var newBotCount = Utilities.GetPlayers().Count(p => p?.IsBot == true);
                ReplyToCommand(player, $" [GrokPlugin] Bot count after spawning: {newBotCount}");
                if (newBotCount > currentBots)
                {
                    ReplyToCommand(player, $" [GrokPlugin] Successfully added {newBotCount - currentBots} bots!");
                }
                else
                {
                    ReplyToCommand(player, $" [GrokPlugin] Bot spawning may have failed. Current: {newBotCount}, Previous: {currentBots}");
                }
            });
        });
    }

    private bool CanUsePlugin(CCSPlayerController? player)
    {
        if (_config?.Enabled != true || player?.IsValid != true)
            return false;

        if (_config.WhitelistedSteamIds?.Any() == true)
        {
            return _config.WhitelistedSteamIds.Contains(player.SteamID.ToString());
        }

        return true; 
    }

    private void ShowHelp(CCSPlayerController player, bool isAdmin = false)
    {
        if (player == null) return;
        
        // Basic commands available to everyone
        var helpMessage = "\n[GrokPlugin] Available Commands:\n";
        helpMessage += "  @grok <message> - Chat with the AI\n";
        helpMessage += "  @grok help - Show this help message\n";

        // Admin-only commands
        if (isAdmin)
        {
            helpMessage += "\n[Admin Commands]\n";
            helpMessage += $"  {_config.AdminFlag}help - Show admin help\n";
            helpMessage += $"  {_config.AdminFlag}status - Show plugin status\n";
            helpMessage += $"  {_config.AdminFlag}spawn <T/CT> <count> - Add bots to a team\n";
            helpMessage += $"  {_config.AdminFlag}kickbots - Remove all bots\n";
            helpMessage += $"  {_config.AdminFlag}mode <chat/admin> - Switch between chat and admin modes\n";
            helpMessage += $"  {_config.AdminFlag}restart - Restart the game\n";
        }

        // Send the help message in chunks to respect chat limits
        var chunks = ChunkTextForChat(helpMessage, 127, " ");
        if (chunks != null)
        {
            foreach (var chunk in chunks)
            {
                player.PrintToChat(chunk);
            }
        }
    }

    private void ProcessAdminCommand(CCSPlayerController player, string message)
    {
        if (_config == null) return;
        
        // Check for help command first
        if (string.IsNullOrWhiteSpace(message) || message.Trim().Equals("help", StringComparison.OrdinalIgnoreCase))
        {
            ShowHelp(player, true);
            return;
        }
        
        // Process bot commands
        if (message.Trim().StartsWith("spawn ", StringComparison.OrdinalIgnoreCase) || 
            message.Trim().StartsWith("add ", StringComparison.OrdinalIgnoreCase) ||
            message.Trim().StartsWith("bot_add", StringComparison.OrdinalIgnoreCase))
        {
            var parts = message.Split(' ');
            var count = 1;
            var team = "";

            // Find the number in the message
            foreach (var part in parts)
            {
                if (int.TryParse(part, out var parsedCount) && parsedCount > 0)
                {
                    count = Math.Min(parsedCount, 12); // Max 12 bots at once
                    break;
                }
            }

            // Determine team (default to T)
            if (message.IndexOf("ct", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                team = "ct";
            }
            else
            {
                team = "t";
            }

            // Add all bots at once
            for (int i = 0; i < count; i++)
            {
                Server.ExecuteCommand($"bot_add {team}");
            }
            
            // Set bot difficulty
            Server.ExecuteCommand("bot_difficulty 3");
            
            // Send admin confirmation
            SendChatMessage(player, $"Added {count} bots to {team.ToUpper()} team", true);
            return;
        }
        
        // Forward to regular message processing for other admin commands
        ProcessPlayerMessage(player, message);
    }
    
    private void ProcessPlayerMessage(CCSPlayerController player, string message)
    {
        if (player != null)
        {
            player.PrintToChat(message);
        }
    }

    private void ReplyToCommand(CCSPlayerController? player, string message)
    {
        if (player != null)
        {
            player.PrintToChat(message);
        }
        else
        {
            Logger.LogInformation(message);
        }
    }
}