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

namespace ChatifyBridgeServer
{
    public class Program
    {
        public static async Task Main(string[] args)
        {
            var builder = WebApplication.CreateBuilder(args);
            builder.Services.AddCors(options =>
            {
                options.AddDefaultPolicy(policy =>
                {
                    policy.AllowAnyOrigin().AllowAnyMethod().AllowAnyHeader();
                });
            });

            builder.Services.AddControllers();
            builder.Services.AddSingleton<SupabaseService>();
            builder.Services.AddSingleton<OpenAIService>();

            var app = builder.Build();
            app.UseCors();
            app.UseRouting();
            app.MapControllers();

            await app.RunAsync("http://localhost:3001");
        }
    }

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

        public async Task<(bool valid, ApiKeyData? config, string plan)> CheckAccessAsync(string apiKey)
        {
            var cleanKey = apiKey?.Trim();
            try
            {
                var keyResponse = await _httpClient.GetAsync($"{SupabaseUrl}/rest/v1/api_keys?key_value=eq.{cleanKey}");
                if (!keyResponse.IsSuccessStatusCode) return (false, null, "Free");
                var keyContent = await keyResponse.Content.ReadAsStringAsync();
                var keyDataList = JsonSerializer.Deserialize<List<ApiKeyData>>(keyContent);
                var keyData = keyDataList?.FirstOrDefault();
                if (keyData == null || keyData.Status != "active") return (false, null, "Free");
                var subResponse = await _httpClient.GetAsync($"{SupabaseUrl}/rest/v1/subscriptions?user_id=eq.{keyData.UserId}&status=eq.active");
                var subContent = await subResponse.Content.ReadAsStringAsync();
                var subDataList = JsonSerializer.Deserialize<List<SubscriptionData>>(subContent);
                var subData = subDataList?.FirstOrDefault();
                return (subData != null && subData.Status == "active", keyData, subData?.Plan ?? "Free");
            }
            catch { return (false, null, "Free"); }
        }

        public async Task<string> GenerateUniqueSessionIdAsync() => $"sess_{Guid.NewGuid().ToString().Substring(0, 13)}";

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

    public class OpenAIService
    {
        private readonly OpenAIClient _client;
        public OpenAIService() => _client = new OpenAIClient("REEMPLAZAR_CON_TU_OPENAI_KEY");

        public async Task<string> GetResponseAsync(List<ChatMessage> history, string newMessage, string context, List<object> faqs)
        {
            try
            {
                var chatClient = _client.GetChatClient("gpt-4o-mini");
                var messages = new List<OpenAI.Chat.ChatMessage> { new SystemChatMessage($"Limit 40 words. Context: {context}.") };
                foreach (var msg in history)
                {
                    if (msg.role == "user") messages.Add(new UserChatMessage(msg.content));
                    else messages.Add(new AssistantChatMessage(msg.content));
                }
                messages.Add(new UserChatMessage(newMessage));
                ChatCompletion completion = await chatClient.CompleteChatAsync(messages);
                return completion.Content[0].Text;
            }
            catch { return "Error"; }
        }
    }

    [ApiController]
    [Route("")]
    public class BridgeController : ControllerBase
    {
        private readonly SupabaseService _supabase;
        private readonly OpenAIService _openai;
        public BridgeController(SupabaseService supabase, OpenAIService openai) { _supabase = supabase; _openai = openai; }

        [HttpPost("client-widget-bridge")]
        public async Task<IActionResult> PostClientWidget([FromQuery] string api_key, [FromBody] ChatRequest request)
        {
            var (valid, config, _) = await _supabase.CheckAccessAsync(api_key);
            if (!valid || config == null) return StatusCode(403);
            string sessionId = string.IsNullOrEmpty(request.session_id) ? await _supabase.GenerateUniqueSessionIdAsync() : request.session_id;
            var history = await _supabase.GetHistoryAsync(sessionId);
            var aiResponse = await _openai.GetResponseAsync(history, request.message, config.Context, config.ManualFaqs);
            await _supabase.InsertLogAsync(config.Id, "message", new { session_id = sessionId });
            return Ok(new { response = aiResponse, session_id = sessionId });
        }
    }

    public class ChatRequest { public string message { get; set; } = ""; public string session_id { get; set; } = ""; }
    public class ChatMessage { public string role { get; set; } = ""; public string content { get; set; } = ""; }
    public class ApiKeyData { [JsonPropertyName("id")] public string Id { get; set; } = ""; [JsonPropertyName("user_id")] public string UserId { get; set; } = ""; [JsonPropertyName("status")] public string Status { get; set; } = ""; [JsonPropertyName("context")] public string Context { get; set; } = ""; [JsonPropertyName("manual_faqs")] public List<object> ManualFaqs { get; set; } = new(); }
    public class SubscriptionData { [JsonPropertyName("plan")] public string Plan { get; set; } = ""; [JsonPropertyName("status")] public string Status { get; set; } = ""; [JsonPropertyName("ends_at")] public string EndsAt { get; set; } = ""; }
    public class ChatMemory { [JsonPropertyName("history_json")] public List<ChatMessage> HistoryJson { get; set; } = new(); }
}