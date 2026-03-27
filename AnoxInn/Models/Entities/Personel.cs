using System;
using System.Collections.Generic;

namespace AxonInn.Models.Entities;

public partial class Personel
{
    public long Id { get; set; }

    public string Adi { get; set; } = null!;

    public string Soyadi { get; set; } = null!;

    public long DepartmanRef { get; set; }

    public DateTime? DogumTarihi { get; set; }

    public byte? MedenHali { get; set; }

    public byte AktifMi { get; set; }

    public string TelefonNumarasi { get; set; } = null!;

    public string MailAdresi { get; set; } = null!;

    public string Sifre { get; set; } = null!;

    public byte Yetki { get; set; }



    public virtual Departman DepartmanRefNavigation { get; set; } = null!;

    public virtual ICollection<Gorev> Gorevs { get; set; } = new List<Gorev>();

    public virtual ICollection<PersonelFotograf> PersonelFotografs { get; set; } = new List<PersonelFotograf>();

    public byte MailOnayliMi { get; set; } // SQL'de TinyInt (Default 0 olmalı)
    public string? VerificationToken { get; set; } // SQL'de nvarchar (Nullable olmalı)
}
