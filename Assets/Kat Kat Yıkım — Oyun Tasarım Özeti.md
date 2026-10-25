# Kat Kat Yıkım — Oyun Tasarım Özeti

Oct 24, 2026 · @Fatih
## Özet

Sonsuz yükseklikte bir kuleyi yukarıdan aşağı kat kat patlatıyoruz; amaç, içindeki eşyaları iplerle sabitleyip enkazın güvenli bölgenin dışına taşmasını önlemek. Tek cümleyle: **kaosu planla, patlat ve en kritik anda müdahale et.**

- **Tür:** 3D fizik bulmacası, roguelite ilerleme
- **Kamera:** 360° döndürülebilir, kat izolasyonlu
- **Motor:** Unity, hazır ip fiziği paketi
- **Oturum süresi:** 10–20 dakika, skor = inilen kat sayısı

**Tema bağlantısı:** Çöküş oyunun başarısızlık durumu değil, oyuncunun yönettiği şeyin kendisi. Bina her durumda yıkılıyor; oyuncunun kontrol ettiği şey nasıl yıkılacağı.

## Çekirdek döngü

&#91;embedded content: çekirdek döngü · 4 adım, 1 karar\]

Her kat aynı döngüden geçiyor; oyunu heyecanlı kılan, patlama anındaki müdahale adımı. Can bittiğinde döngü kırılıyor ve skor, inilen kat sayısı oluyor.

## Mekanikler

İp sabitleme ve patlama sırası oyunun iki ana kolu; ağır çekim müdahalesi bunları heyecanlı bir ana bağlıyor.

### İp ve sabitleme

| Tür | Dayanım | Maliyet | Özellik |
| --- | --- | --- | --- |
| İnce ip | Düşük | Ucuz | Hafif eşyalar için |
| Çelik halat | Yüksek | Pahalı | Ağır eşyaları tutar, ağırdır |
| Elastik ip | Orta | Orta | Şoku emer, eşya sallanır |
| File | Orta | Orta | Birçok küçük eşyayı tek seferde sarar |

- **İp bütçesi:** Her katta sınırlı uzunlukta ip var; her şeyi bağlamak mümkün değil, öncelik seçmek gerekiyor.
- **Bağlama noktaları:** Sadece kolonlara ve zemin halkalarına bağlanabilir. Kolon patlamada giderse ipi de boşa çıkar.
- **Paketleme:** Küçük eşyalar ağır bir eşyaya bağlanıp tek kütle haline getirilebilir.

### Patlama sırası

Patlayıcıların yeri sabit; oyuncu sadece ateşleme sırasını ve gecikmesini ayarlıyor. Önce giden kolon binanın düşeceği yönü belirliyor, bu yüzden ipler bu yöne göre planlanmalı.

### Ağır çekim müdahalesi

Patlama sırasında 2–3 kez zaman yavaşlıyor. Bu anlarda oyuncu kopmak üzere olan bir ipi destekleyebilir ya da uçan bir eşyaya yeni ip atabilir. Oyun, sadece planlamadan ibaret kalmıyor.

### Eşyalar

| Eşya | Sorun | Çözüm ipucu |
| --- | --- | --- |
| Buzdolabı | Çok ağır, ince ipi koparır | Çelik halat |
| Cam vitrin | Kırılınca parçaları saçılır | Önce file ile sar |
| Gaz tüpü | Serbest kalırsa ikinci patlama | Mutlaka sabitle |
| Su tankı / akvaryum | Sıvı yerdeki eşyaları kaydırır | Yere yakın sabitle |
| Kitaplık | Devrilince içi dağılır | Duvara ya da kolona bağla |
| Kedi | Bağlanamaz, canlı | Taşıyarak kurtar |

### Puanlama

- Kat çevresinde sarı-siyah şeritle çizilmiş bir **güvenli bölge** var.
- Bölge dışına taşan her parça ceza; komşu binaya çarpan parça büyük ceza.
- **3 can:** Büyük bir eşya bölgeden çıkarsa bir can gidiyor.
- Kullanılmayan ip bir sonraki kata aktarılıyor.

## Sonsuz kule ve ilerleme

Kule o kadar yüksek ki dibi görünmüyor; her tur en üst kat yıkılıyor ve enkaz bir alt katın tavanında, güvenli bölge içinde kalmalı. Gerçekte de yüksek binalar bazen yukarıdan aşağı kat kat sökülüyor, yani kurgu mantıklı.

### Zorluk artışı

- Katta daha fazla eşya
- Daha dar ip bütçesi
- Değiştiriciler: rüzgâr, kırılgan eşyalar, gaz tüpleri, sıvılar
- Kat tipleri: ofis, mutfak, kütüphane, laboratuvar, depo

### Katlar arası yükseltme

Her kattan sonra 3 seçenekten biri seçiliyor; örneğin daha güçlü ip, ekstra ağır çekim hakkı, file, daha geniş güvenli bölge. Her oturum farklı bir yapıya dönüşüyor.

### Patron katları

Tekrarı kırmak için her 5 katta bir el yapımı özel kat: dev akvaryumlu kat, kasası olan banka katı gibi.

### Bitiş

3 can bitince oyun biter; skor, inilen kat sayısı.

## Görsel tarz ve kamera

Low-poly, oyuncak/maket görünümü: hızlı üretilir, fizikle iyi anlaşır ve patlamalar sevimli ama tatmin edici durur.

### Görsel dil

- Az sayıda, parlak, düz renk; doku yerine renkle ayrım
- Bebek evi gibi kesit görünüm, isteğe bağlı hafif tilt-shift
- Bağlanmış eşyalar hafif parlıyor; ip gerginliği yeşilden kırmızıya dönüyor; güvenli bölge sarı-siyah şerit
- Dikey düzen: üstte gökyüzü (yıkılmış katlar), ortada aydınlık aktif kat, altta loş bekleyen katlar. Her patlamadan sonra kamera bir kat aşağı süzülüyor.

### Modeller

- **Modüler yapı parçaları:** duvar, zemin, kolon, pencere, kapı. Bina bunlardan kurulduğu için patlamada parçalar zaten ayrık.
- **Eşyalar:** belirgin silüetli, uzaktan tanınan objeler.
- **Kaynaklar:** Kenney ve Quaternius ücretsiz low-poly paketleri.

### Kamera

- 360° döndürme ve yakınlaştırma
- **Kat izolasyonu:** sadece aktif kat görünür, üsttekiler gizli
- Kameraya yakın duvarlar otomatik şeffaflaşır
- Seçili eşyaya odaklanma, 4 sabit açı arasında hızlı geçiş
- Patlama anında otomatik geniş sinematik açı

## Teknik yaklaşım

Yıkım, Unity içinde gerçek zamanlı fizikle yapılacak; önceden hesaplanmış animasyon ipler ve ağır çekim müdahalesiyle etkileşemez.

### Yıkım sistemi

- Her yapı parçası ayrı bir Rigidbody.
- Parçalar `FixedJoint` ile bağlı, `breakForce` değeri var: bina sağlam durur, zorlanınca kendiliğinden kopar.
- Patlama: ilgili kolonların eklemleri silinir, `AddExplosionForce` uygulanır. Çöküş yönü patlama sırasından doğal olarak çıkar.
- Daha güzel enkaz için duvarlar Blender Cell Fracture ile önceden parçalanabilir.

### Dikkat edilecekler

- **Performans:** Rigidbody sayısını sınırla, yere oturan enkazı birkaç saniye sonra dondur, Layer Collision Matrix ile gereksiz çarpışmaları kapat.
- **Hızlı küçük eşyalar:** `Continuous` çarpışma algılama.
- **Ağır çekim:** `Time.timeScale` düşürülürken `Time.fixedDeltaTime` da aynı oranda küçültülmeli.
- **İp paketi:** Kırılan eklemler ve Rigidbody'lerle uyumu ilk iş küçük bir sahnede test edilmeli. Projenin en riskli noktası.
- **Belirsizlik:** Unity fiziği tam deterministik değil; aynı patlama birebir aynı sonucu vermeyebilir. Önizleme modu eklenirse "yaklaşık gösterim" olarak sunulmalı.

## 48 saatlik kapsam

Önce tek bir katın döngüsü kusursuz çalışmalı; sonsuz yapı ve içerik bunun üstüne eklenir.

### Mutlaka olmalı

- [ ] Tek oda prototipi: modüler parçalar, iki eşya, ip, patlama (ilk 2–3 saat)
- [ ] Patlama sırası ile yön kontrolü
- [ ] Ağır çekim müdahalesi
- [ ] 3 ip türü
- [ ] Güvenli bölge ve puanlama, 3 can
- [ ] Prosedürel kat üretimi ve zorluk artışı

### Zaman kalırsa

- [ ] Katlar arası yükseltmeler
- [ ] Zincirleme etkiler (gaz tüpü, sıvı)
- [ ] Patron katları

### Cila

- [ ] Ağır çekim kamerası ve patlama efektleri (toz, enkaz)
- [ ] Önizleme modu
- [ ] Ses ve müzik

**Hızlı sıfırlama** baştan beri olmalı: patlat, bak, tek tuşla planlamaya dön. Test süresini ciddi kısaltır.
