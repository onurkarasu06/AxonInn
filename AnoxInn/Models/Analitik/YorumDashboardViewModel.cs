namespace AxonInn.Models.Analitik
{
    public class YorumDashboardViewModel
    {
        // 1. Üst Kısım: KPI Kartları
        public KpiKartlariVerisi KpiVerileri { get; set; }

        // 2. Grafikler
        public DuyguPastaGrafigiVerisi DuyguGrafik { get; set; }
        public DepartmanBasariGrafigiVerisi DepartmanGrafik { get; set; }
        public HisPolarGrafigiVerisi HisPolarGrafik { get; set; }
        public KelimeBarGrafigiVerisi KelimeGrafik { get; set; }
        public UlkeMemnuniyetGrafigiVerisi UlkeGrafik { get; set; }
        public KonaklamaTipiGrafigiVerisi KonaklamaGrafik { get; set; }
        public AylikTrendGrafigiVerisi TrendGrafik { get; set; }
        public string Yil { get; set; }
    }
}