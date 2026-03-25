using AxonInn.Models;
using Microsoft.EntityFrameworkCore;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System.Text;
using System.Text.Json;

namespace AxonInn.Apify
{
    public class YorumIslem
    {
        // PERFORMANS: Socket Exhaustion'ı önlemek için HttpClient her zaman statik olmalıdır.
        private static readonly HttpClient _httpClient = new HttpClient { Timeout = TimeSpan.FromMinutes(3) };

        public async Task<List<Yorum>> VeritabaniYorumListGetirAsync(long hotelID, AxonInnContext context)
        {
            // Sadece okuma yapılan listelerde AsNoTracking RAM tüketimini %40 düşürür
            return await context.Yorum.AsNoTracking()
                                      .Where(y => y.HotelRef == hotelID)
                                      .OrderByDescending(y => y.MisafirYorumTarihi)
                                      .ToListAsync();
        }

        public async Task<List<Yorum>> GeminiAnaliziOlanVeritabaniYorumListGetirAsync(long hotelID, AxonInnContext context)
        {
            // Bu veriler güncelleneceği için Tracking açık bırakıldı
            return await context.Yorum.Where(y => y.HotelRef == hotelID && y.GeminiAnalizYapildiMi == 1)
                                      .OrderByDescending(y => y.MisafirYorumTarihi)
                                      .ToListAsync();
        }

        public async Task<List<Yorum>> GeminiAnaliziOlmayanVeritabaniYorumListGetirAsync(long hotelID, AxonInnContext context)
        {
            // Bu veriler güncelleneceği için Tracking açık bırakıldı
            return await context.Yorum.Where(y => y.HotelRef == hotelID && y.GeminiAnalizYapildiMi != 1)
                                      .OrderByDescending(y => y.MisafirYorumTarihi)
                                      .ToListAsync();
        }

        public async Task<List<Yorum>> TripadvisorYorumGetirAsync(long hotelID, int getirilecekKayitAdeti, string apiToken)
        {
            string hotelUrl = "https://www.tripadvisor.com.tr/Hotel_Review-g12078952-d23426767-Reviews-Alarcha_Hotels_Resort-Boztepe_Manavgat_Turkish_Mediterranean_Coast.html";
            string ApiUrl = $"https://api.apify.com/v2/acts/maxcopell~tripadvisor-reviews/run-sync-get-dataset-items?token={apiToken}";

            var payload = new { startUrls = new[] { new { url = hotelUrl } }, maxItemsPerQuery = getirilecekKayitAdeti };
            using StringContent request = new StringContent(JsonConvert.SerializeObject(payload), Encoding.UTF8, "application/json");

            HttpResponseMessage response = await _httpClient.PostAsync(ApiUrl, request);
            response.EnsureSuccessStatusCode();

            string jsonResponse = await response.Content.ReadAsStringAsync();
            JArray apiResults = JArray.Parse(jsonResponse);

            List<Yorum> yorumList = new List<Yorum>();
            foreach (JToken item in apiResults)
                yorumList.Add(new Yorum(item, hotelID));

            return yorumList;
        }

        public async Task<int> TripadvisorYorumKaydetAsync(long hotelID, List<Yorum> yorumList, AxonInnContext context)
        {
            if (yorumList == null || yorumList.Count == 0) return 0;

            // PERFORMANS: Döngü içinde veritabanı yormamak için gelen ID'leri O(1) hızındaki HashSet ile tarıyoruz
            var gelenYorumIDler = yorumList.Select(y => y.MisafirYorumId).Where(id => !string.IsNullOrEmpty(id)).ToList();
            var mevcutYorumIdler = await context.Yorum
                                                .AsNoTracking()
                                                .Where(y => y.HotelRef == hotelID && gelenYorumIDler.Contains(y.MisafirYorumId))
                                                .Select(y => y.MisafirYorumId)
                                                .ToListAsync();

            var idSet = new HashSet<string>(mevcutYorumIdler);
            var eklenecekYorumlar = new List<Yorum>();

            foreach (Yorum yorum in yorumList)
            {
                if (!string.IsNullOrEmpty(yorum.MisafirYorumId) && !idSet.Contains(yorum.MisafirYorumId))
                {
                    eklenecekYorumlar.Add(yorum);
                }
            }

            if (eklenecekYorumlar.Any())
            {
                await context.Yorum.AddRangeAsync(eklenecekYorumlar); // Toplu Ekleme (Batch Insert)
                await context.SaveChangesAsync();
            }

            return eklenecekYorumlar.Count;
        }

        public async Task<string> GeminiYorumAnaliziYapAsync(Yorum tekYorum, string geminiApiKey)
        {
            string geminiUrl = $"https://generativelanguage.googleapis.com/v1beta/models/gemini-2.5-flash:generateContent?key={geminiApiKey}";
            string tekYorumJson = System.Text.Json.JsonSerializer.Serialize(tekYorum);

            string prompt = $@"Sen AxonInn otel yönetim sistemi için çalışan kıdemli bir turizm ve veri analistisin. Sana aşağıda JSON formatında TEK BİR misafir yorumu veriyorum. Lütfen bu yorumu; misafirin ülkesini, konaklama tipini ve tarihini de göz önünde bulundurarak aşağıdaki JSON kalıbına göre çok boyutlu analiz et.ÖNEMLİ KURAL: Lütfen bana SADECE aşağıdaki JSON formatında cevap ver. Herhangi bir markdown (```json vb.) veya açıklama cümlesi KULLANMA.İstenen Kalıp:{{  ""DuyguAnalizi"": {{    ""Durum"": """", ""Skor"": 0, ""BaskinHis"": """", ""IlgiliDepartman"": """" }},  ""AnahtarKelimeler"": [], ""ProfilBeklentisi"": """", ""KulturelHassasiyet"": """", ""SezonsalDurum"": """", ""KisaOzet"": """", ""AcilDurumVarMi"": false, ""MudureTavsiye"": """" }}Analiz edilecek misafir yorumu verisi:{tekYorumJson}";

            var requestBody = new
            {
                contents = new[] { new { parts = new[] { new { text = prompt } } } },
                generationConfig = new { response_mime_type = "application/json" }
            };

            using var content = new StringContent(System.Text.Json.JsonSerializer.Serialize(requestBody), Encoding.UTF8, "application/json");

            // PERFORMANS: Thread kitleyen (GetAwaiter) kodlar silindi
            HttpResponseMessage response = await _httpClient.PostAsync(geminiUrl, content);

            if (response.IsSuccessStatusCode)
            {
                string responseString = await response.Content.ReadAsStringAsync();
                using JsonDocument doc = JsonDocument.Parse(responseString);
                return doc.RootElement.GetProperty("candidates")[0]
                                      .GetProperty("content")
                                      .GetProperty("parts")[0]
                                      .GetProperty("text")
                                      .GetString() ?? "";
            }
            else
            {
                throw new Exception($"Gemini API Hatası: {await response.Content.ReadAsStringAsync()}");
            }
        }
    }
}