using Microsoft.EntityFrameworkCore;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace AxonInn.Models
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

        public List<Yorum> TripadvisorYorumGetirApifyApi(long hotelID, int getirilecekKayitAdeti, string apiToken)
        {
            string hotelUrl = "https://www.tripadvisor.com.tr/Hotel_Review-g12078952-d23426767-Reviews-Alarcha_Hotels_Resort-Boztepe_Manavgat_Turkish_Mediterranean_Coast.html";
            string ApiUrl = $"https://api.apify.com/v2/acts/maxcopell~tripadvisor-reviews/run-sync-get-dataset-items?token={apiToken}";

            var payload = new { startUrls = new[] { new { url = hotelUrl } }, maxItemsPerQuery = getirilecekKayitAdeti };

            using StringContent requestContent = new StringContent(JsonConvert.SerializeObject(payload), Encoding.UTF8, "application/json");

            // ASENKRON YERİNE SENKRON İSTEK (Send) KULLANIYORUZ
            using var requestMessage = new HttpRequestMessage(HttpMethod.Post, ApiUrl) { Content = requestContent };
            using HttpResponseMessage response = _httpClient.Send(requestMessage);
            response.EnsureSuccessStatusCode();

            // İÇERİĞİ SENKRON OLARAK OKUYORUZ
            string jsonResponse = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
            JArray apiResults = JArray.Parse(jsonResponse);

            List<Yorum> yorumList = new List<Yorum>();
            foreach (JToken item in apiResults)
            {
                yorumList.Add(new Yorum(item, hotelID));
            }

            return yorumList;
        }

        public List<Yorum> TripadvisorYorumGetirRapidApi(long hotelID, long tripadvisorHotelID, int getirilecekKayitAdeti, string rapidApiKey)
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

                string jsonPayload = JsonConvert.SerializeObject(payload);

                var request = new HttpRequestMessage
                {
                    Method = HttpMethod.Post,
                    RequestUri = new Uri(apiUrl),
                    Headers =
                    {
                        { "x-rapidapi-key", rapidApiKey },
                        { "x-rapidapi-host", "travel-advisor.p.rapidapi.com" },
                        { "User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36" }
                    },
                    Content = new StringContent(jsonPayload, Encoding.UTF8, "application/json")
                };

                using HttpResponseMessage response = _httpClient.Send(request);
                response.EnsureSuccessStatusCode();

                string jsonResponse = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();

                if (string.IsNullOrWhiteSpace(jsonResponse))
                    break;

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
                {
                    break;
                }

                sayfaSayaci += 20;
            }

            return yorumList;
        }

        private string TemizleHTML(string text)
        {
            if (string.IsNullOrEmpty(text)) return text;
            return Regex.Replace(text, "<.*?>", string.Empty).Trim();
        }

        public int TripadvisorYorumKaydet(long hotelID, List<Yorum> yorumList, AxonInnContext context)
        {
            if (yorumList == null || yorumList.Count == 0) return 0;

            var gelenYorumIDler = yorumList.Select(y => y.MisafirYorumId).Where(id => !string.IsNullOrEmpty(id)).ToList();

            var mevcutYorumIdler = context.Yorum
                                          .AsNoTracking()
                                          .Where(y => y.HotelRef == hotelID && gelenYorumIDler.Contains(y.MisafirYorumId))
                                          .Select(y => y.MisafirYorumId)
                                          .ToList();

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
                context.Yorum.AddRange(eklenecekYorumlar);
                context.SaveChanges();
            }

            return eklenecekYorumlar.Count;
        }

        public string GeminiYorumAnaliziYap(List<Yorum> yorumList, string geminiApiKey)
        {
            string geminiUrl = $"https://generativelanguage.googleapis.com/v1beta/models/gemini-2.5-flash:generateContent?key={geminiApiKey}";
            string yorumListJson = System.Text.Json.JsonSerializer.Serialize(yorumList);
            string prompt = $@"Sen AxonInn otel yönetim sistemi için çalışan kıdemli bir turizm ve veri analistisin.                                                                                     
                                Sana aşağıda JSON formatında BİRDEN FAZLA misafir yorumu içeren bir liste veriyorum.                                                                                     
                                Lütfen listedeki HER BİR yorumu; misafirin ülkesini, konaklama tipini ve tarihini de göz önünde bulundurarak aşağıdaki JSON kalıbına göre çok boyutlu analiz et ve sonuçları bir JSON DİZİSİ (Array) olarak döndür.                                                                                    

                                ÖNEMLİ KURALLAR:                                                             
                                1- Lütfen bana SADECE aşağıdaki JSON formatında bir dizi olarak cevap ver. Herhangi bir markdown (```json vb.) veya açıklama cümlesi KULLANMA.                                                            
                                2- 'Skor' değeri KESİNLİKLE 1 ile 100 arasında bir TAM SAYI (Integer) olmalıdır. Ondalıklı (0.95 gibi) değerler KULLANMA.                                          
                                3- 'DuyguAnalizi.Durum' değeri İSTİSNASIZ OLARAK şu 5 ifadeden SADECE BİRİ olmalıdır: ""Çok İyi"", ""İyi"", ""Nötr"", ""Kötü"", ""Çok Kötü"". Başka hiçbir kelime kullanma.                     
                                4- 'DuyguAnalizi.IlgiliDepartman' değeri KESİNLİKLE şu listedeki departmanlardan SADECE BİRİ olmalıdır: ""Misafir İlişkileri"", ""Genel Tesis"", ""Personel"", ""Animasyon"", ""Teknik Servis"", ""Kat Hizmetleri"", ""Yiyecek ve İçecek"", ""Satış ve Pazarlama"", ""Ön Büro"", ""İnsan Kaynakları"", ""Güvenlik"". (Yorumda birden fazla departmandan bahsedilse bile, aralarından en ağırlıklı/en baskın olan SADECE TEK BİR departmanı seç. Asla virgül kullanma ve listede olmayan hiçbir isim uydurma).
                                5- 'DuyguAnalizi.BaskinHis' değeri KESİNLİKLE şu listedeki hislerden SADECE BİRİ olmalıdır: ""Neşe"", ""Rahatlık"", ""Memnuniyet"", ""Rahatsızlık"", ""Hayal Kırıklığı"", ""Tekrar Gelme İsteği"", ""Memnuniyetsizlik"", ""Öfke ve Haksızlık Hissi"", ""Şikayet"", ""Harika"", ""Coşku"". Yorumdaki duyguya en uygun olanı seç ve listede olmayan hiçbir kelime kullanma.

                                İstenen Kalıp (Bu kalıptaki objelerden oluşan bir dizi dönmelisin):                                                                                    
                                [                                                                                            
                                  {{                                                                                                        
                                      ""YorumId"": ""(Gelen verideki Id değerini buraya tam olarak yaz)"",                                                                                                        
                                      ""DuyguAnalizi"": {{                                
                                          ""Durum"": ""(Sadece: Çok İyi, İyi, Nötr, Kötü, Çok Kötü)"",                                
                                          ""Skor"": 0,                                
                                          ""BaskinHis"": ""(Listeden SADECE TEK BİR his)"",                                
                                          ""IlgiliDepartman"": ""(Listeden SADECE en baskın olan TEK BİR departman)""                            
                                      }},                                                                                                        
                                      ""AnahtarKelimeler"": [],                                                                                                         
                                      ""ProfilBeklentisi"": """",                                                                                                         
                                      ""KulturelHassasiyet"": """",                                                                                                         
                                      ""SezonsalDurum"": """",                                                                                                         
                                      ""KisaOzet"": """",                                                                                                         
                                      ""AcilDurumVarMi"": false,                                                                                                         
                                      ""MudureTavsiye"": """"                                                                                             
                                  }}                                                                                    
                                ]                                                                                    
                                Analiz edilecek misafir yorumları listesi:                                                                                    
                                {yorumListJson}";
            var requestBody = new
            {
                contents = new[] { new { parts = new[] { new { text = prompt } } } },
                generationConfig = new { response_mime_type = "application/json" }
            };

            using var content = new StringContent(System.Text.Json.JsonSerializer.Serialize(requestBody), Encoding.UTF8, "application/json");
            using var request = new HttpRequestMessage(HttpMethod.Post, geminiUrl) { Content = content };

            try
            {
                using HttpResponseMessage response = _httpClient.Send(request);

                if (response.IsSuccessStatusCode)
                {
                    string responseString = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
                    using JsonDocument doc = JsonDocument.Parse(responseString);
                    return doc.RootElement
                              .GetProperty("candidates")[0]
                              .GetProperty("content")
                              .GetProperty("parts")[0]
                              .GetProperty("text")
                              .GetString() ?? "[]";
                }
                else
                {
                    string errorDetail = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
                    throw new Exception($"Gemini API Hatası (Kod: {response.StatusCode}): {errorDetail}");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Analiz sırasında hata oluştu: {ex.Message}");
                throw;
            }
        }

        public async Task<int> YorumlariPartilerHalindeIsleAsync(List<Yorum> dbYorumList, YorumIslem yorumIslem, string apiKey, AxonInnContext _context)
        {
            int partiBuyuklugu = 15;
            int toplamPartiSayisi = (int)Math.Ceiling((double)dbYorumList.Count / partiBuyuklugu);
            int basariylaIslenenToplam = 0;
            for (int i = 0; i < toplamPartiSayisi; i++)
            {
                var suAnkiParti = dbYorumList.Skip(i * partiBuyuklugu).Take(partiBuyuklugu).ToList();
                int buPartideIslenen = await TekPartiyiIsleVeKaydetAsync(suAnkiParti, yorumIslem, apiKey, _context);
                basariylaIslenenToplam += buPartideIslenen;
                if (i < toplamPartiSayisi - 1)
                    await Task.Delay(40000);
            }
            return basariylaIslenenToplam;
        }

        public async Task<int> TekPartiyiIsleVeKaydetAsync(List<Yorum> parti, YorumIslem yorumIslem, string apiKey, AxonInnContext _context)
        {
            string topluAnalizCevabi = yorumIslem.GeminiYorumAnaliziYap(parti, apiKey);
            int islenenSayisi = await JsonCevabiniYorumlaraUygulaAsync(topluAnalizCevabi, parti, _context);
            return islenenSayisi;
        }

        public async Task<int> JsonCevabiniYorumlaraUygulaAsync(string jsonCevabi, List<Yorum> parti, AxonInnContext _context)
        {
            int basariliIslem = 0;
            using JsonDocument doc = JsonDocument.Parse(jsonCevabi);
            JsonElement root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Array)
                return basariliIslem;

            foreach (JsonElement analizItem in root.EnumerateArray())
            {
                if (analizItem.TryGetProperty("YorumId", out JsonElement idElement))
                {
                    string currentYorumId = idElement.ToString().Trim();
                    var dbYorum = parti.FirstOrDefault(y => y.MisafirYorumId == currentYorumId);

                    if (dbYorum != null)
                    {
                        dbYorum.GeminiVerileriniIsle(analizItem.GetRawText());
                        if (dbYorum.GeminiAnalizYapildiMi == 1)
                        {
                            await _context.SaveChangesAsync();
                            basariliIslem++;
                        }
                    }
                }
            }

            return basariliIslem;
        }
    }
}