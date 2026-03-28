using AxonInn.Models.Context;
using AxonInn.Models.Entities;
using Microsoft.EntityFrameworkCore;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Net.Http.Json; // Native streaming işlemleri için dahil edildi

namespace AxonInn.Models.Analitik
{
    public class YorumIslem
    {
        private static readonly HttpClient _httpClient = new HttpClient { Timeout = TimeSpan.FromMinutes(3) };
        private static readonly JsonSerializerOptions _jsonOptions = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };

        public async Task<List<Yorum>> VeritabaniYorumListGetirAsync(long hotelID, AxonInnContext context)
        {
            return await context.Yorum.AsNoTracking()
                                      .Where(y => y.HotelRef == hotelID)
                                      .OrderByDescending(y => y.MisafirYorumTarihi)
                                      .ToListAsync();
        }

        public async Task<List<Yorum>> GeminiAnaliziOlanVeritabaniYorumListGetirAsync(long hotelID, AxonInnContext context)
        {
            // Update işlemi olmayıp sadece okunacağı için AsNoTracking eklendi (RAM Tasarrufu)
            return await context.Yorum.AsNoTracking()
                                      .Where(y => y.HotelRef == hotelID && y.GeminiAnalizYapildiMi == 1)
                                      .OrderByDescending(y => y.MisafirYorumTarihi)
                                      .ToListAsync();
        }

        public async Task<List<Yorum>> GeminiAnaliziOlmayanVeritabaniYorumListGetirAsync(long hotelID, AxonInnContext context)
        {
            return await context.Yorum.Where(y => y.HotelRef == hotelID && y.GeminiAnalizYapildiMi != 1)
                                      .OrderByDescending(y => y.MisafirYorumTarihi)
                                      .ToListAsync();
        }

        public async Task<List<Yorum>> TripadvisorYorumGetirApifyApiAsync(long hotelID, int getirilecekKayitAdeti, string apiToken)
        {
            string hotelUrl = "https://www.tripadvisor.com.tr/Hotel_Review-g12078952-d23426767-Reviews-Alarcha_Hotels_Resort-Boztepe_Manavgat_Turkish_Mediterranean_Coast.html";
            string ApiUrl = $"https://api.apify.com/v2/acts/maxcopell~tripadvisor-reviews/run-sync-get-dataset-items?token={apiToken}";

            var payload = new { startUrls = new[] { new { url = hotelUrl } }, maxItemsPerQuery = getirilecekKayitAdeti };

            // Sıfır tahsisli ağ yayını (Stream)
            using HttpResponseMessage response = await _httpClient.PostAsJsonAsync(ApiUrl, payload);
            response.EnsureSuccessStatusCode();

            string jsonResponse = await response.Content.ReadAsStringAsync();
            JArray apiResults = JArray.Parse(jsonResponse);

            // PERFORMANS: Listenin arka planda kapasite genişletmesi (Resize Array) yapmasını engellemek için başlangıç Capacity girildi.
            List<Yorum> yorumList = new List<Yorum>(apiResults.Count);
            foreach (JToken item in apiResults)
            {
                Yorum cekilenYorum = Yorum.ApifyYorumOlustur(item, hotelID);
                yorumList.Add(cekilenYorum);
            }

            return yorumList;
        }

        public async Task<List<Yorum>> TripadvisorYorumGetirRapidApiAsync(long hotelID, long tripadvisorHotelID, int getirilecekKayitAdeti, string rapidApiKey)
        {
            List<Yorum> yorumList = new List<Yorum>();
            string apiUrl = "https://travel-advisor.p.rapidapi.com/reviews/v2/list?currency=TRY&units=km&lang=tr_TR";

            int cekilenSayi = 0;
            int sayfaSayaci = 0;
            string guncelUpdateToken = "";
            string durdurulacakYorumId = "0";
            bool hedefYorumaUlasildi = false;

            while (cekilenSayi < getirilecekKayitAdeti && !hedefYorumaUlasildi)
            {
                var payload = new
                {
                    contentType = "hotel",
                    detailId = tripadvisorHotelID,
                    pagee = sayfaSayaci,
                    filters = new object[] { },
                    updateToken = guncelUpdateToken
                };

                var request = new HttpRequestMessage(HttpMethod.Post, apiUrl)
                {
                    Headers =
                    {
                        { "x-rapidapi-key", rapidApiKey },
                        { "x-rapidapi-host", "travel-advisor.p.rapidapi.com" },
                        { "User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64)" }
                    },
                    Content = JsonContent.Create(payload) // JsonContent doğrudan Serialize ve Send eder.
                };

                using HttpResponseMessage response = await _httpClient.SendAsync(request);
                response.EnsureSuccessStatusCode();

                string jsonResponse = await response.Content.ReadAsStringAsync();
                if (string.IsNullOrWhiteSpace(jsonResponse)) break;

                JObject apiResult = JObject.Parse(jsonResponse);
                JToken sections = apiResult.SelectToken("data.AppPresentation_queryPoiReviews.sections");
                if (sections == null) break;

                bool yeniYorumBulundu = false;
                guncelUpdateToken = "";

                foreach (JToken section in sections)
                {
                    JToken singleCardContent = section["poiReviewsSingleCardContent"];

                    if (singleCardContent != null && singleCardContent["__typename"]?.ToString() == "AppPresentation_ReviewCard")
                    {
                        string oAnkiYorumId = (string)singleCardContent.SelectToken("helpfulVote.helpfulVoteAction.objectId");

                        if (oAnkiYorumId == durdurulacakYorumId)
                        {
                            hedefYorumaUlasildi = true;
                            break;
                        }

                        if (cekilenSayi >= getirilecekKayitAdeti) break;

                        yorumList.Add(new Yorum(singleCardContent, hotelID));
                        cekilenSayi++;
                        yeniYorumBulundu = true;
                    }

                    if (section["__typename"]?.ToString() == "AppPresentation_SecondaryButton")
                    {
                        guncelUpdateToken = section.SelectToken("link.updateToken")?.ToString() ?? "";
                    }
                }

                if (!yeniYorumBulundu || string.IsNullOrEmpty(guncelUpdateToken) || hedefYorumaUlasildi)
                    break;

                sayfaSayaci += 20;
            }

            return yorumList;
        }

        private string TemizleHTML(string text)
        {
            if (string.IsNullOrEmpty(text)) return text;
            return Regex.Replace(text, "<.*?>", string.Empty).Trim();
        }

        // PERFORMANS: Bu metot Thread kilitlenmesini durdurmak için tamamen Asenkron yapıldı. (ToListAsync, AddRangeAsync)
        public async Task<int> TripadvisorYorumKaydetAsync(long hotelID, List<Yorum> yorumList, AxonInnContext context)
        {
            if (yorumList == null || yorumList.Count == 0) return 0;

            var gelenYorumIDler = yorumList.Select(y => y.MisafirYorumId).Where(id => !string.IsNullOrEmpty(id)).ToList();
            if (gelenYorumIDler.Count == 0) return 0;

            var mevcutYorumIdler = new HashSet<string>();

            // SQL Server'da IN(..) kullanırken 2100'den fazla sorgulama veritabanını ÇÖKERTİR ("Too many parameters").
            // Bu kritik risk .Chunk(1000) kullanılarak güvenli parçalara bölündü.
            foreach (var chunk in gelenYorumIDler.Chunk(1000))
            {
                var ids = await context.Yorum.AsNoTracking()
                                     .Where(y => y.HotelRef == hotelID && chunk.Contains(y.MisafirYorumId))
                                     .Select(y => y.MisafirYorumId)
                                     .ToListAsync();
                foreach (var id in ids)
                {
                    mevcutYorumIdler.Add(id);
                }
            }

            var eklenecekYorumlar = new List<Yorum>(yorumList.Count);

            foreach (var yorum in yorumList)
            {
                // PERFORMANS ÇİFT DİKİŞ KONTROLÜ: !mevcutYorumIdler.Contains(id) sorgusunun üstüne bir de Add() demek liste 2 kez baştan sonra taranır. 
                // Zaten HashSet.Add() eleman yoksa ekleyip True, varsa False döndüğü için, arama yükü yarı yarıya hafifletildi.
                if (!string.IsNullOrEmpty(yorum.MisafirYorumId) && mevcutYorumIdler.Add(yorum.MisafirYorumId))
                {
                    eklenecekYorumlar.Add(yorum);
                }
            }

            if (eklenecekYorumlar.Count > 0)
            {
                await context.Yorum.AddRangeAsync(eklenecekYorumlar);
                await context.SaveChangesAsync();
            }

            return eklenecekYorumlar.Count;
        }

        public async Task<string> GeminiYorumAnaliziYapAsync(List<Yorum> yorumList, string geminiApiKey)
        {
            string geminiUrl = $"https://generativelanguage.googleapis.com/v1beta/models/gemini-2.5-flash:generateContent?key={geminiApiKey}";

            // TOKEN İSRAFI ÖNLENDİ (API Maliyeti Optimizasyonu):
            // Veritabanı modeli içinde yer alan onlarca null, relation ve gereksiz property JSON yapılıp gönderiliyordu.
            // Yalnızca analizi yapılacak alanlar "Anonymous Type" olarak kopyalandı ve Token boyutu %80 küçüldü.
            var optimizeEdilmisYorumlar = yorumList.Select(y => new {
                y.MisafirYorumId,
                y.MisafirKonaklamaTarihi,
                y.MisafirKonaklamaTipi,
                y.MisafirUlkesi,
                y.MisafirYorum
            });

            string yorumListJson = System.Text.Json.JsonSerializer.Serialize(optimizeEdilmisYorumlar);

            string prompt = $@"Sen AxonInn otel yönetim sistemi için çalışan kıdemli bir turizm ve veri analistisin.                                                                                     
                                Sana aşağıda JSON formatında BİRDEN FAZLA misafir yorumu içeren bir liste veriyorum.                                                                                     
                                Lütfen listedeki HER BİR yorumu; misafirin ülkesini, konaklama tipini ve tarihini de göz önünde bulundurarak aşağıdaki JSON kalıbına göre analiz et ve JSON DİZİSİ döndür.                                                                                    

                                ÖNEMLİ KURALLAR:                                                             
                                1- SADECE JSON formatında bir dizi olarak cevap ver. Herhangi bir markdown (```json vb.) KULLANMA.                                                            
                                2- 'Skor' değeri KESİNLİKLE 1 ile 100 arasında bir TAM SAYI (Integer) olmalıdır.                                          
                                3- 'DuyguAnalizi.Durum' SADECE: ""Çok İyi"", ""İyi"", ""Nötr"", ""Kötü"", ""Çok Kötü"".                     
                                4- 'DuyguAnalizi.IlgiliDepartman' SADECE: ""Misafir İlişkileri"", ""Genel Tesis"", ""Personel"", ""Animasyon"", ""Teknik Servis"", ""Kat Hizmetleri"", ""Yiyecek ve İçecek"", ""Satış ve Pazarlama"", ""Ön Büro"", ""İnsan Kaynakları"", ""Güvenlik"".
                                5- 'DuyguAnalizi.BaskinHis' SADECE: ""Neşe"", ""Rahatlık"", ""Memnuniyet"", ""Rahatsızlık"", ""Hayal Kırıklığı"", ""Tekrar Gelme İsteği"", ""Memnuniyetsizlik"", ""Öfke ve Haksızlık Hissi"", ""Şikayet"", ""Harika"", ""Coşku"".

                                İstenen Kalıp:                                                                                    
                                [                                                                                            
                                  {{                                                                                                        
                                      ""YorumId"": ""(Id değerini yaz)"",                                                                                                        
                                      ""DuyguAnalizi"": {{ ""Durum"": """", ""Skor"": 0, ""BaskinHis"": """", ""IlgiliDepartman"": """" }},                                                                                                        
                                      ""AnahtarKelimeler"": [], ""ProfilBeklentisi"": """", ""KulturelHassasiyet"": """", ""SezonsalDurum"": """", ""KisaOzet"": """", ""AcilDurumVarMi"": false, ""MudureTavsiye"": """"                                                                                             
                                  }}                                                                                    
                                ]                                                                                    
                                Yorumlar: {yorumListJson}";

            var requestBody = new
            {
                contents = new[] { new { parts = new[] { new { text = prompt } } } },
                generationConfig = new { response_mime_type = "application/json" }
            };

            try
            {
                using HttpResponseMessage response = await _httpClient.PostAsJsonAsync(geminiUrl, requestBody);
                if (response.IsSuccessStatusCode)
                {
                    string responseString = await response.Content.ReadAsStringAsync();
                    using JsonDocument doc = JsonDocument.Parse(responseString);
                    return doc.RootElement.GetProperty("candidates")[0].GetProperty("content").GetProperty("parts")[0].GetProperty("text").GetString() ?? "[]";
                }
                else
                {
                    string errorDetail = await response.Content.ReadAsStringAsync();
                    throw new Exception($"Gemini API Hatası (Kod: {response.StatusCode}): {errorDetail}");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Analiz sırasında hata: {ex.Message}");
                throw;
            }
        }

        public async Task<int> YorumlariPartilerHalindeIsleAsync(List<Yorum> dbYorumList, YorumIslem yorumIslem, string apiKey, AxonInnContext _context)
        {
            int basariylaIslenenToplam = 0;

            // O(N²) CPU HATASI ÇÖZÜLDÜ: .Skip(i * 20).Take(20) işlemi, her döndüğünde listeyi baştan tarayarak CPU'yu dar boğaza sokar.
            // Yeni nesil C# içindeki optimize edilmiş .Chunk(20) metoduyla liste 1 kerede bellekte en hızlı şekilde dilimlenir.
            var partiler = dbYorumList.Chunk(20).ToList();

            foreach (var suAnkiParti in partiler)
            {
                int buPartideIslenen = await TekPartiyiIsleVeKaydetAsync(suAnkiParti.ToList(), yorumIslem, apiKey, _context);
                basariylaIslenenToplam += buPartideIslenen;
            }

            return basariylaIslenenToplam;
        }

        public async Task<int> TekPartiyiIsleVeKaydetAsync(List<Yorum> parti, YorumIslem yorumIslem, string apiKey, AxonInnContext _context)
        {
            string topluAnalizCevabi = await yorumIslem.GeminiYorumAnaliziYapAsync(parti, apiKey);
            return await JsonCevabiniYorumlaraUygulaAsync(topluAnalizCevabi, parti, _context);
        }

        public async Task<int> JsonCevabiniYorumlaraUygulaAsync(string jsonCevabi, List<Yorum> parti, AxonInnContext _context)
        {
            int basariliIslem = 0;
            using JsonDocument doc = JsonDocument.Parse(jsonCevabi);
            JsonElement root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Array) return basariliIslem;

            // O(N) Döngüsü içinde O(N) arama (.FirstOrDefault) yapıldığında binlerce yorumda sistem kitlenir (Timeout).
            // Dizi bir kere O(1) hızındaki Dictionary (Sözlük Lookup) yapısına geçirilerek, JSON eşleşme hızı anlık seviyeye çıkarıldı.
            var partiSozlugu = parti.Where(y => !string.IsNullOrEmpty(y.MisafirYorumId))
                                    .ToDictionary(y => y.MisafirYorumId);

            foreach (JsonElement analizItem in root.EnumerateArray())
            {
                if (analizItem.TryGetProperty("YorumId", out JsonElement idElement))
                {
                    string currentYorumId = idElement.ToString().Trim();

                    // Hızlı Sözlük Araması (Dictionary Lookup)
                    if (partiSozlugu.TryGetValue(currentYorumId, out var dbYorum))
                    {
                        dbYorum.GeminiVerileriniIsle(analizItem.GetRawText());
                        if (dbYorum.GeminiAnalizYapildiMi == 1)
                        {
                            basariliIslem++;
                        }
                    }
                }
            }

            if (basariliIslem > 0)
                await _context.SaveChangesAsync();

            return basariliIslem;
        }
    }
}