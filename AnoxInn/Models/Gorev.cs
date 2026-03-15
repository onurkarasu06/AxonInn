using System;
using System.Collections.Generic;

namespace AxonInn.Models;

public partial class Gorev
{
    public long Id { get; set; }

    public long PersonelRef { get; set; }

    public byte Durum { get; set; }

    public string Gorev1 { get; set; } = null!;

    public DateTime KayitTarihi { get; set; }

    public DateTime? CozumBaslamaTarihi { get; set; }

    public DateTime? CozumBitisTarihi { get; set; }

    public string? PersonelNotu { get; set; }

    public virtual ICollection<GorevFotograf> GorevFotografs { get; set; } = new List<GorevFotograf>();

    public virtual Personel PersonelRefNavigation { get; set; } = null!;
}
