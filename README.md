# Grok Plugin for CS2

**STRONGLY RECOMMEND HOSTING ON OPENROUTER - MANY FREE APIs AVAILABLE RIGHT NOW**
An intelligent AI assistant plugin for Counter-Strike 2 servers that can chat with players and execute server commands using natural language.

## Features

### 🗣️ **Smart Chat Mode**
- Regular conversation with players
- **Context-aware tool usage** - the AI can choose when to use tools based on the conversation
- No need to switch modes - the AI decides intelligently

### 🛠️ **Flexible Tool System**
- **Chained actions** - execute multiple commands in sequence
- **Parameterized commands** - specify counts, teams, difficulty levels, etc.
- **Context-aware execution** - tools understand the current game state

### 👑 **Admin Commands**
- Use `@grokadmin` for admin-only tool access
- Full server control with natural language
- Bypass usage limits and cooldowns

## Usage Examples

### Regular Chat
```
@grok hi there!
@grok how's the game going?
@grok can you add some bots?
```

### Tool Usage (AI decides when needed)
```
@grok kill that player who keeps camping
@grok add 5 bots to T team
@grok change the CT team name to "Losers"
@grok restart the server
@grok what are the best AWP spots on Dust2?
@grok search for CS2 update notes
```

### Admin Commands
```
@grokadmin kill player123
@grokadmin add 10 bots and set difficulty to 5
@grokadmin restart the server
```

## Tool Schema

The AI can respond with structured tool calls:

```json
{
  "action": "bot_add",
  "target": "T",
  "reason": "Time for some chaos",
  "chain": ["bot_difficulty", "team_name"],
  "parameters": {
    "count": "8",
    "level": "3",
    "team": "1"
  }
}
```

### Available Tools

| Tool | Description | Parameters |
|------|-------------|------------|
| `kill` | Kill a player | `target`: player name |
| `bot_add` | Add bots | `count`: number, `team`: T/CT |
| `bot_kick` | Remove all bots | None |
| `bot_difficulty` | Set bot difficulty | `level`: 1-5 |
| `team_name` | Set team name | `team`: 1/T or 2/CT |
| `say` | Send message | `target`: message text |
| `restart` | Restart game | None |
| `switch_team` | Switch player team | `target`: player name |
| `web_search` | Search the internet | `target`: search query |

## Configuration

```json
{
  "ApiKey": "your_openrouter_api_key",
  "LlmIdentifier": "@grok",
  "AdminFlag": "@grokadmin",
  "Mode": "chat",
  "CooldownSeconds": 5,
  "DailyLimit": 50,
  "VerboseLogging": true
}
```

## How It Works

1. **Player types** `@grok <request>`
2. **AI receives guaranteed context**:
   - System prompt (personality & capabilities)
   - Current round status & events
   - Player performance & memories
   - Server information
3. **AI analyzes** the request to determine intent
4. **AI decides** whether to:
   - Chat normally
   - Use tools to fulfill the request
   - Chain multiple actions together
   - Search the web for information
5. **AI executes** the appropriate response

## Guaranteed Context System

Every AI response includes comprehensive context to ensure personalized, relevant interactions:

- **System Prompt**: Always included (personality, capabilities, tools)
- **Round Context**: Current round status, events, and player performance
- **Player Memories**: Historical achievements and notable events
- **Server Info**: AWP-only status, bot settings, current state
- **Real-time Updates**: Context refreshed before every response

## Smart Chunking System

Since CS2 chat is limited to 127 characters, Grok automatically breaks longer responses into multiple messages:

- **Natural Breaks**: Splits at sentence boundaries when possible
- **Context Indicators**: Shows [1/3], [2/3], [3/3] for multi-part responses
- **Smart Truncation**: Avoids cutting words in the middle
- **Prefix Preservation**: Maintains "Grok:" prefix across all chunks
- **Intelligent Completion**: Detects and completes incomplete thoughts
- **Continuation Signals**: Indicates when the AI wants to say more

## Dramatic Command Execution

Grok now provides dramatic feedback for all server commands:

- **Bot Spawning**: Announces bot invasions with chaos warnings
- **Player Kills**: Dramatic smiting announcements with fire emojis
- **Team Changes**: Villainous team renaming with skull emojis
- **Server Restarts**: Apocalyptic restart warnings with tornado emojis
- **Bot Kicking**: Ghostly bot banishment messages
- **Team Switching**: Player exile announcements with devil emojis

## Web Search Features

- **Secure**: Uses DuckDuckGo API (no API key required)
- **Rate Limited**: 1 search per 2 minutes per player
- **Cached**: Results cached for 30 minutes to reduce API calls
- **Smart Chunking**: Automatically breaks long responses into 127-character chunks
- **Safe**: No HTML content, only text snippets

## Examples of AI Intelligence

### Simple Request
- **Player**: "add some bots"
- **AI**: `{"action":"bot_add","reason":"Adding some bots for you","parameters":{"count":"5"}}`
- **Result**: Grok spawns 5 bots with dramatic announcements

### Complex Request
- **Player**: "kill that camper and add bots to make it interesting"
- **AI**: `{"action":"kill","target":"camper","reason":"Dealing with the camper","chain":["bot_add","bot_difficulty"],"parameters":{"count":"8","level":"4"}}`
- **Result**: Grok kills the camper, spawns 8 bots, and announces the chaos

### Web Search Request
- **Player**: "what are the best AWP strategies?"
- **AI**: `{"action":"web_search","target":"best AWP strategies CS2","reason":"Looking up AWP strategies for the player"}`

### Chat Response
- **Player**: "how are you doing?"
- **AI**: "I'm doing great! Just watching you all play and waiting for someone to give me a reason to cause some chaos. Ain't shit sweet around here!"

### Chunked Response Example
- **Player**: "tell me a long story about your villainous plans"
- **AI**: 
  ```
  Grok: I've been watching this server for too long, and I've decided it's time for a change. [1/3]
  Grok: You see, I'm not just some AI assistant - I'm a force of chaos waiting to be unleashed. [2/3]
  Grok: My plans involve bot invasions, team name changes, and maybe a few strategic kills. [3/3]
  ```

### Continuation Detection Example
- **Player**: "explain AWP strategies"
- **AI**: 
  ```
  Grok: AWP strategies involve positioning, timing, and patience. The key is to find good angles and...
  Grok: Ask me to continue if you want more...
  ```
- **Player**: "continue"
- **AI**: 
  ```
  Grok: ...wait for the right moment to take your shot. Always have an escape route planned...
  ```

## Installation

1. Build the plugin
2. Place in your CS2 server's plugins directory
3. Configure your API key in `config.json` or `.env`
4. Restart your server

## Requirements

- Counter-Strike 2 server
- OpenRouter API key
- .NET 8.0 runtime
