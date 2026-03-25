namespace AxonInn.Models
{
    public class GeminiAnalizSonucu
    {
        public DuyguDetay DuyguAnalizi { get; set; }
        public List<string> AnahtarKelimeler { get; set; }
        public string ProfilBeklentisi { get; set; }
        public string KulturelHassasiyet { get; set; }
        public string SezonsalDurum { get; set; }
        public string KisaOzet { get; set; }
        public bool AcilDurumVarMi { get; set; }
        public string MudureTavsiye { get; set; }
    }

    public class DuyguDetay
    {
        public string Durum { get; set; }
        public int Skor { get; set; }
        public string BaskinHis { get; set; }
        public string IlgiliDepartman { get; set; }
    }
}

