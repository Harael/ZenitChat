using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Supabase;
using OpenAI;
using OpenAI.Chat;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Mvc;
using System.IO;

/*
 * PROJECT: ZenitChat Bridge Server
 * DESCRIPTION:
 * This application acts as a secure intermediary (bridge) between a client-side widget
 * and external services (OpenAI and Supabase). 
 * * Key functionalities:
 * 1. API Key Validation: Checks if the user has an active subscription in Supabase.
 * 2. Chat Memory: Persists and retrieves conversation history to maintain context.
 * 3. AI Proxy: Forwards messages to OpenAI (GPT-4o-mini) with custom system context.
 * 4. Logging: Tracks API usage for analytics and billing purposes.
 */

namespace ChatifyBridgeServer
{
    public class Program
    {
        public static async Task Main(string[] args)
        {
            var builder = WebApplication.CreateBuilder(args);
            
            // Configure CORS to allow the widget to communicate from any origin
            builder.Services.AddCors(options =>
            {
                options.AddDefaultPolicy(policy =>
                {
                    policy.AllowAnyOrigin().AllowAnyMethod().AllowAnyHeader();
                });
            });

            builder.Services.AddControllers();
            
            // Dependency Injection: Register services as Singletons to reuse instances
            builder.Services.AddSingleton<SupabaseService>();
            builder.Services.AddSingleton<OpenAIService>();

            var app = builder.Build();
            app.UseCors();
            app.UseRouting();
            app.MapControllers();

            // Server execution on port 3001
            await app.RunAsync("http://localhost:3001");
        }
    }

    /*
     * SERVICE: SupabaseService
     * Handles all direct communication with the Supabase REST API to manage
     * users, subscriptions, logs, and chat history.
     */
    public class SupabaseService
    {
        private readonly HttpClient _httpClient;
        private const string SupabaseKey = "REEMPLAZAR_CON_TU_KEY";
        private const string SupabaseUrl = "REEMPLAZAR_CON_TU_URL";

        public SupabaseService()
        {
            _httpClient = new HttpClient();
            _httpClient.DefaultRequestHeaders.Add("apikey", SupabaseKey);
        }

        /* * Verifies if a provided API key exists, is active, and 
         * belongs to a user with an active subscription.
         */
        public async Task<(bool valid, ApiKeyData? config, string plan)> CheckAccessAsync(string apiKey)
        {
            var cleanKey = apiKey?.Trim();
            try
            {
                // Fetch key data
                var keyResponse = await _httpClient.GetAsync($"{SupabaseUrl}/rest/v1/api_keys?key_value=eq.{cleanKey}");
                if (!keyResponse.IsSuccessStatusCode) return (false, null, "Free");
                
                var keyContent = await keyResponse.Content.ReadAsStringAsync();
                var keyDataList = JsonSerializer.Deserialize<List<ApiKeyData>>(keyContent);
                var keyData = keyDataList?.FirstOrDefault();
                
                if (keyData == null || keyData.Status != "active") return (false, null, "Free");

                // Fetch subscription status for the key owner
                var subResponse = await _httpClient.GetAsync($"{SupabaseUrl}/rest/v1/subscriptions?user_id=eq.{keyData.UserId}&status=eq.active");
                var subContent = await subResponse.Content.ReadAsStringAsync();
                var subDataList = JsonSerializer.Deserialize<List<SubscriptionData>>(subContent);
                var subData = subDataList?.FirstOrDefault();
                
                return (subData != null && subData.Status == "active", keyData, subData?.Plan ?? "Free");
            }
            catch { return (false, null, "Free"); }
        }

        public async Task<string> GenerateUniqueSessionIdAsync() => $"sess_{Guid.NewGuid().ToString().Substring(0, 13)}";

        // Retrieves past messages for a specific session to maintain bot "memory"
        public async Task<List<ChatMessage>> GetHistoryAsync(string sessionId)
        {
            try
            {
                var response = await _httpClient.GetAsync($"{SupabaseUrl}/rest/v1/chat_memory?session_id=eq.{sessionId}");
                var content = await response.Content.ReadAsStringAsync();
                var dataList = JsonSerializer.Deserialize<List<ChatMemory>>(content);
                return dataList?.FirstOrDefault()?.HistoryJson ?? new List<ChatMessage>();
            }
            catch { return new List<ChatMessage>(); }
        }

        // Logs an event (like a message sent) to Supabase for usage tracking
        public async Task InsertLogAsync(string apiKeyId, string eventType, object eventData)
        {
            try
            {
                var logEntry = new { api_key_id = apiKeyId, event_type = eventType, event_data = eventData, created_at = DateTime.UtcNow };
                var content = new StringContent(JsonSerializer.Serialize(logEntry), System.Text.Encoding.UTF8, "application/json");
                await _httpClient.PostAsync($"{SupabaseUrl}/rest/v1/api_key_logs", content);
            }
            catch {}
        }

        // Saves the updated conversation history back to the database
        public async Task UpsertMemoryAsync(string apiKeyId, string sessionId, List<ChatMessage> history)
        {
            try
            {
                var memory = new { api_key_id = apiKeyId, session_id = sessionId, history_json = history, updated_at = DateTime.UtcNow };
                var content = new StringContent(JsonSerializer.Serialize(memory), System.Text.Encoding.UTF8, "application/json");
                await _httpClient.PostAsync($"{SupabaseUrl}/rest/v1/chat_memory", content);
            }
            catch {}
        }
    }

    /*
     * SERVICE: OpenAIService
     * Wrapper for the OpenAI SDK to generate chat completions using GPT-4o-mini.
     */
    public class OpenAIService
    {
        private readonly OpenAIClient _client;
        public OpenAIService() => _client = new OpenAIClient("REEMPLAZAR_CON_TU_OPENAI_KEY");

        public async Task<string> GetResponseAsync(List<ChatMessage> history, string newMessage, string context, List<object> faqs)
        {
            try
            {
                var chatClient = _client.GetChatClient("gpt-4o-mini");
                
                // Inject system instructions (System Message) and provide specific knowledge context
                var messages = new List<OpenAI.Chat.ChatMessage> { 
                    new SystemChatMessage($"Limit 40 words. Context: {context}. Knowledge base: {JsonSerializer.Serialize(faqs)}") 
                };

                // Reconstruct history so the AI knows what was said before
                foreach (var msg in history)
                {
                    if (msg.role == "user") messages.Add(new UserChatMessage(msg.content));
                    else messages.Add(new AssistantChatMessage(msg.content));
                }

                messages.Add(new UserChatMessage(newMessage));
                
                ChatCompletion completion = await chatClient.CompleteChatAsync(messages);
                return completion.Content[0].Text;
            }
            catch { return "Error processing your request."; }
        }
    }

    /*
     * CONTROLLER: BridgeController
     * The main entry point for the Web Widget. Orchestrates validation, history, and AI response.
     */
    [ApiController]
    [Route("")]
    public class BridgeController : ControllerBase
    {
        private readonly SupabaseService _supabase;
        private readonly OpenAIService _openai;
        public BridgeController(SupabaseService supabase, OpenAIService openai) { _supabase = supabase; _openai = openai; }

        /*
         * ENDPOINT: POST /client-widget-bridge
         * The primary endpoint used by the frontend to send a user message and get an AI response.
         */
        [HttpPost("client-widget-bridge")]
        public async Task<IActionResult> PostClientWidget([FromQuery] string api_key, [FromBody] ChatRequest request)
        {
            // 1. Security check
            var (valid, config, _) = await _supabase.CheckAccessAsync(api_key);
            if (!valid || config == null) return StatusCode(403);

            // 2. Session management
            string sessionId = string.IsNullOrEmpty(request.session_id) || request.session_id == "null" 
                ? await _supabase.GenerateUniqueSessionIdAsync() 
                : request.session_id;

            // 3. Context Retrieval
            var history = await _supabase.GetHistoryAsync(sessionId);
            
            // 4. AI Generation
            var aiResponse = await _openai.GetResponseAsync(history, request.message, config.Context, config.ManualFaqs);

            // 5. Cleanup and Persistence (Log the usage and save new history)
            await _supabase.InsertLogAsync(config.Id, "message", new { session_id = sessionId });
            
            var newHistory = history.Concat(new[] { 
                new ChatMessage { role = "user", content = request.message }, 
                new ChatMessage { role = "assistant", content = aiResponse } 
            }).TakeLast(10).ToList(); // Keep only last 10 messages to save tokens/storage
            
            await _supabase.UpsertMemoryAsync(config.Id, sessionId, newHistory);

            return Ok(new { response = aiResponse, session_id = sessionId });
        }
    }

    // --- DATA MODELS ---
    public class ChatRequest { public string message { get; set; } = ""; public string session_id { get; set; } = ""; }
    public class ChatMessage { public string role { get; set; } = ""; public string content { get; set; } = ""; }
    public class ApiKeyData { 
        [JsonPropertyName("id")] public string Id { get; set; } = ""; 
        [JsonPropertyName("user_id")] public string UserId { get; set; } = ""; 
        [JsonPropertyName("status")] public string Status { get; set; } = ""; 
        [JsonPropertyName("context")] public string Context { get; set; } = ""; 
        [JsonPropertyName("manual_faqs")] public List<object> ManualFaqs { get; set; } = new(); 
    }
    public class SubscriptionData { [JsonPropertyName("plan")] public string Plan { get; set; } = ""; [JsonPropertyName("status")] public string Status { get; set; } = ""; [JsonPropertyName("ends_at")] public string EndsAt { get; set; } = ""; }
    public class ChatMemory { [JsonPropertyName("history_json")] public List<ChatMessage> HistoryJson { get; set; } = new(); }
}