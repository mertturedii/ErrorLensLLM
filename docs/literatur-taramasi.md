# Literatür Taraması

**Proje:** Makine Öğrenmesi Tabanlı Uygulama Log Anomali Tespiti ve Akıllı Olay Yönetim Platformunun Geliştirilmesi

Bu tarama, projenin beş bileşenine karşılık gelen çalışmaları ele alır: log ayrıştırma, log tabanlı anomali tespiti, benzer
hataların gruplanması, olay ilişkilendirme ve kök neden analizi, büyük dil modelleriyle (LLM) olay açıklama. Her bölümün
sonunda o alanın projede nasıl karşılandığı yazılıdır. Künyeler yayın sayfalarından (ACM, IEEE, USENIX, IJCAI, arXiv)
doğrulanmıştır; DOI'si bulunanlarda DOI verilmiştir.

## 1. Genel çerçeve ve derleme çalışmaları

Log analizi, yazılım güvenilirliği mühendisliğinin yerleşik bir alanıdır. He ve arkadaşları [1], 23 yıla yayılan 158 çalışmayı
log ayrıştırma, anomali tespiti, arıza tahmini ve arıza teşhisi başlıkları altında derler. Notaro, Cardoso ve Gerndt [2], aynı
problemi daha geniş AIOps (yapay zekâ destekli BT operasyonları) açısından ele alır ve arıza yönetimi yöntemlerini müdahale
zamanına ve çözdükleri probleme göre 5 kategori ve 14 alt kategoride sınıflandırır. Landauer
ve arkadaşları [3] ise yalnız derin öğrenme tabanlı log anomali tespitine odaklanan sistematik bir tarama yapar; nicel bir
karşılaştırma sunmaz, ancak ham ve yapısız log verisini sinir ağlarına uygun biçime getirmenin güçlüğünü ve değerlendirme
pratiğindeki açıkları vurgular.

Bu proje, [2]'deki sınıflandırmada **tespit** (anomali skoru) ile **kök neden analizi** (olay ilişkilendirme, kök neden
sıralaması, LLM açıklaması) arasında yer alır.

## 2. Log ayrıştırma (log parsing)

Yapısız log satırlarını sabit bir şablona ve değişken parametrelere ayırmak, neredeyse tüm sonraki yöntemlerin ön koşuludur.
**Drain** [4], sabit derinlikli bir ağaç ile çevrimiçi (akış hâlinde) ayrıştırma yapar ve ayrıştırıcı karşılaştırmalarında
en iyi sonuç veren yöntemlerden biridir. Zhu ve arkadaşları [5], 13 ayrıştırıcıyı 16 veri setinde karşılaştırmış ve hiçbir
ayrıştırıcının tüm veri setlerinde ve tüm ölçütlerde öne çıkmadığını göstermiştir. Jiang ve arkadaşları [6], bu değerlendirmenin
küçük örneklemlere dayandığını belirterek ortalama 3,6 milyon satırlık 14 veri setinden oluşan **Loghub-2.0**'ı yayımlamış ve
15 ayrıştırıcıyı yeniden değerlendirmiştir.

Son yıllarda ayrıştırma da dil modellerine kaymıştır. **LogPPT** [7], az sayıda etiketli örnekle istem ayarı (prompt tuning)
yapan bir ön eğitimli dil modeli kullanır. **LILAC** [8], bir LLM'i bağlam içi öğrenmeyle ayrıştırıcı olarak kullanır;
LLM'in tutarsız çıktısını ve maliyetini, şablonları saklayıp düzelten uyarlanabilir bir önbellekle azaltır.

**Projedeki karşılığı.** Collector, yapılandırılmış log gönderen uygulamalarda Serilog'un kendi mesaj şablonunu kullanır;
düz metin loglarda Drain çalıştırır. Proje kapsamında yapılan deney, Drain'in özgün benzerlik ölçüsünün değişken (wildcard)
konumlarını hiç benzer saymadığı için yüksek eşiklerde dağıldığını göstermiştir. Değişkenleri yok sayan "sabit token
benzerliği" ile HDFS gruplama doğruluğu 0,846'dan 0,998'e çıkmış (`docs/experiments/hdfs-parsing.md`), aynı değişiklik BGL'de
özgün ölçünün 5.000'i aşan şablona dağılmasını önleyerek 412 şablon üretmiştir (`docs/experiments/bgl.md`). LLM tabanlı
ayrıştırma [7, 8] her log satırı için model çağrısı ya da önbellek gerektirdiğinden kapsam dışında bırakılmıştır.

## 3. Log tabanlı anomali tespiti

### 3.1 Klasik yöntemler

Xu ve arkadaşları [9], HDFS loglarını blok kimliğine göre oturumlara ayırıp olay sayım vektörleri üzerinde temel bileşen
analizi (PCA) uygulamış; bugün en çok kullanılan HDFS veri seti bu çalışmadan gelir. He ve arkadaşları [10], üç denetimli ve
üç denetimsiz yöntemi iki üretim veri setinde karşılaştırmış ve bunları **loglizer** araç takımı olarak yayımlamıştır.
**Isolation Forest** [11], nadir noktaların rastgele bölmelerle daha kısa yolda yalıtıldığı fikrine dayanan denetimsiz bir
aykırı değer yöntemidir ve log sayım vektörlerinde temel yöntem olarak sık kullanılır.

### 3.2 Derin öğrenme yöntemleri

**DeepLog** [12], normal oturumlarda bir LSTM'i bir sonraki log olayını tahmin etmeye eğitir; gerçek olay modelin en olası
*g* tahmini arasında değilse oturumu anomali sayar. **LogAnomaly** [13], şablonların anlamsal bilgisini *template2vec* ile
vektörleştirerek sıralı ve nicel anomalileri birlikte yakalar ve iki yeniden eğitim arasında ortaya çıkan yeni şablonların
yanlış alarm üretmesini azaltmayı hedefler. **LogRobust** [14], gerçek loglarda daha önce görülmemiş olay ve dizilerin sık
olduğunu ampirik olarak gösterir; bu kararsızlığın kaynakları loglama ifadelerinin evrimi ve işleme gürültüsüdür. Yöntem,
olayları anlamsal vektörlere dönüştürüp dikkat mekanizmalı bir Bi-LSTM ile sınıflandırır. **LogBERT** [15], Transformer
kodlayıcıyı maskelenmiş log anahtarı tahmini ve hiperküre hacmi küçültme görevleriyle öz-denetimli olarak eğitir.
**NeuralLog** [16] ise ayrıştırma hatalarının tespit başarısını düşürdüğünü göstererek ayrıştırmayı tamamen atlar ve ham
mesajları BERT temsilleriyle bir Transformer'a verir.

### 3.3 Değerlendirme sorunları

Bu alandaki yüksek F1 değerlerinin ne ölçtüğü sorgulanmaktadır. Le ve Zhang [17], yaygın derin öğrenme modellerini yeniden
değerlendirmiş ve eğitim verisinin **rastgele mi yoksa kronolojik mi** seçildiğinin yarı-denetimli modellerin sonucunu
önemli ölçüde etkilediğini göstermiştir. Landauer, Skopik ve Wurzenberger [18], HDFS, BGL, Thunderbird, OpenStack ve Hadoop
gibi standart veri setlerini incelemiş; anomalilerin çoğunun sıralı (dizisel) biçimde ortaya çıkmadığını ve bu veri
setlerinde yüksek tespit oranı için gelişmiş tekniklere gerek olmadığını bulmuştur.

**Projedeki karşılığı.** Proje, kural tabanlı temel yöntemleri, Isolation Forest'ı ve DeepLog'u HDFS ve BGL üzerinde aynı
bölmeyle karşılaştırır. HDFS'te DeepLog en iyi sonucu vermiştir (F1 0,800; kural 0,610; Isolation Forest 0,585). BGL'de
sonuçlar iki literatür bulgusunu doğrudan yeniden üretir:

- [17] ile uyumlu olarak, sıralama bölme yöntemine bağlıdır: kronolojik bölmede test pencerelerinin %47,6'sı eğitimde hiç
  görülmemiş şablon içerir ve DeepLog'un F1'i 0,131'e düşer; rastgele bölmede 0,963'e çıkar.
- [18] ile uyumlu olarak, basit bir kural (pencerede FATAL seviyeli satır olması) kronolojik bölmede en iyi yöntemdir
  (F1 0,733), çünkü BGL etiketleri büyük ölçüde FATAL seviyeli alarm satırlarıdır.

Canlı sistemde, [14] ve [13]'ün işaret ettiği "yeni şablon" sorununa karşı modeller 30 dakikada bir yeniden eğitilir.
LogBERT [15] ve anlamsal temsiller [13, 14, 16] bu projede uygulanmamış, gelecek çalışma olarak bırakılmıştır.

## 4. Benzer hataların gruplanması

Büyük sistemlerde aynı sorun çok sayıda farklı log ya da hata kaydı olarak görünür. **LogCluster** [19], Microsoft
hizmetlerinde log dizilerini ağırlıklandırılmış vektörlere dönüştürüp kümeleyerek mühendislerin incelemesi gereken log
sayısını azaltmıştır; her kümeden temsilci diziler seçilir ve daha önce görülmüş kümeler elenir. **Log3C** [20] bu fikri,
log kümelerini sistem performans göstergeleriyle (KPI) ilişkilendirerek etkili sorunları öne çıkaracak biçimde genişletir.

**Projedeki karşılığı.** Collector, her hatayı tür, normalleştirilmiş mesaj ve kod konumundan oluşan bir parmak izine
(fingerprint) göre gruplar. Bu kesin eşleşme, aynı arızayı farklı modüllerde ayrı gruplara böler. [19]'a benzer biçimde
hata grupları, terimlerinin TF-IDF vektörleri üzerinde kosinüs benzerliği ve ortalama bağlantılı hiyerarşik kümelemeyle
birleştirilir. Etiketli veri olmadığı için bu bileşen nicel olarak değerlendirilmemiştir.

## 5. Olay yönetimi, ilişkilendirme ve kök neden analizi

Chen ve arkadaşları [21], Microsoft'taki iki yıllık olay yönetimi pratiğini inceler ve olay yönetimi için bir AIOps çerçevesi
(IcM BRAIN) sunar. Aynı ekibin **LiDAR** çalışması [22], birbiriyle bağlantılı
olayları, metin açıklamalarını ve geçmişte bağlanmış olayların yapısal bilgisini birleştiren bir derin öğrenme modeliyle
tespit eder.

Dağıtımlarla (deploy) ilişkilendirme ayrı bir alt alan oluşturur. **Gandalf** [23], Azure'da arıza sinyallerini süren
yazılım dağıtımlarıyla uzamsal ve zamansal olarak ilişkilendirir, hangi dağıtımın sorumlu olabileceğini bir topluluk
sıralama algoritmasıyla belirler ve kötü bir dağıtımı yaygın bir kesintiye dönüşmeden durdurur.

Mikroservislerde dağıtık izler (trace) kök neden yerelleştirmesinde kullanılır. **MicroRank** [24], normal ve anormal
izlerden PageRank ile spektrum analizini birleştirerek gecikme sorunlarına yol açan servis örneklerini sıralar.

**Projedeki karşılığı.** Projenin olay ilişkilendiricisi bu üç fikri küçük ölçekte birleştirir: (i) aynı W3C trace içinde
görülen hata grupları aynı olaya bağlanır ve trace içinde ilk hata veren grup "yukarı akış" kanıtı kazanır ([24]'teki iz
analizinin basit bir karşılığı); (ii) [23]'teki gibi hatalar zamansal olarak dağıtımlarla ilişkilendirilir ve yalnız sürüm
değiştiren dağıtım şüpheli sayılır; (iii) [22]'deki bağlantılı olay fikrine benzer biçimde, ilişkili hata grupları yüzlerce
ayrı alarm yerine tek olayda toplanır. Kök neden skoru kural tabanlıdır ve her skor Türkçe kanıt cümleleriyle gerekçelendirilir.
Hata enjeksiyonu deneyinde yöntem, temel senaryolarda 9/9, zor senaryolarda (v2) 12/12 Top-1 doğruluğa ulaşmıştır. Ancak v2
düzeltmeleri bu senaryolar görüldükten sonra yapıldığı için bu sonuç genelleme kanıtı değildir.

## 6. Büyük dil modelleriyle kök neden önerisi ve açıklama

Ahmed ve arkadaşları [25], Microsoft'taki 40.000'i aşkın olay üzerinde çeşitli LLM'leri sıfır atışlı (zero-shot) ve ince
ayarlı biçimde değerlendirerek kök neden ve giderme adımı önerisi üretmiş, sonuçları olay sahipleriyle insan
değerlendirmesinden de geçirmiştir. **RCACopilot** [26], tanı bilgisini birden çok kaynaktan toplayan aşama ile LLM'in kök
neden kategorisini tahmin edip açıklama yazdığı aşamayı ayırır; tanı toplama bileşeni Microsoft'ta yıllardır kullanılmaktadır.
**LogPrompt** [27], alan içi eğitim verisi olmadan, istem stratejileriyle LLM'lerden hem log analizi sonucu hem de insan
tarafından okunabilir açıklama üretir.

**Projedeki karşılığı.** Projenin LLM katmanı [26]'daki iki aşamalı ayrıma benzer: tanı bilgisi (sıralı kök neden adayları
ve kanıtları, şüpheli dağıtım, örnek istek zinciri, stack trace, canlı anomali notları) önce kural tabanlı sistemce toplanır,
ardından LLM bunları açıklar ve sonraki adımları önerir. Projede iki bilinçli farklılık vardır:

1. **Karar kural tabanlı sıralamada kalır.** LLM sıralamayı değiştirmez, yalnız açıklar; katılmıyorsa ayrı bir alanda
   gerekçesini yazar. Böylece geliştirici geri bildirimi, LLM metnini değil sıralamayı değerlendirir.
2. **Kişisel veri maskelenir.** Modele giden tüm metinler collector'da maskelenmiştir; kurumsal loglar söz konusu olduğunda
   bu, KVKK açısından bir gerekliliktir.

LLM katmanının açıklama kalitesi henüz değerlendirilmemiştir; [25]'teki gibi bir insan değerlendirmesi gelecek çalışmadır.

## 7. Veri setleri

**Loghub** [28], yapay zekâ destekli log analizi için yaygın kullanılan log veri setlerini bir arada sunar. Bu projede
kullanılan iki veri seti şunlardır:

- **HDFS** [9]: blok kimliği bir oturum tanımlar; etiketler blok düzeyindedir.
- **BGL** [29]: Blue Gene/L süper bilgisayarının RAS logudur; oturum kimliği yoktur, etiketler satır düzeyindeki alarm
  işaretlerinden gelir.

[18]'in eleştirisi bu iki veri seti için de geçerlidir. Bu yüzden proje, sonuçlarını kendi hata enjeksiyonu senaryoları ve
canlı demo ölçümüyle destekler.

## 8. Projenin literatürdeki konumu

| Bileşen | Literatürdeki karşılık | Projede yapılan | Fark / sınırlama |
|---|---|---|---|
| Ayrıştırma | Drain [4], Loghub-2.0 [6], LLM ayrıştırıcılar [7, 8] | Serilog şablonu + Drain (sabit token benzerliği) | LLM ayrıştırma yok |
| Anomali tespiti | DeepLog [12], IF [11], LogBERT [15] | Kural, IF, DeepLog; HDFS + BGL; canlı akışta 10 sn'lik kovalar | LogBERT ve anlamsal temsiller yok |
| Değerlendirme | Le ve Zhang [17], Landauer vd. [18] | Kronolojik ve rastgele bölme birlikte raporlandı | Bulgular literatürle uyumlu |
| Gruplama | LogCluster [19], Log3C [20] | Parmak izi + TF-IDF kümeleme | Nicel değerlendirme yok |
| İlişkilendirme | LiDAR [22], Gandalf [23], MicroRank [24] | Trace, dağıtım ve başlangıç sırasına dayalı kural tabanlı skor | Küçük ölçek, üç servisli demo |
| LLM | Ahmed vd. [25], RCACopilot [26], LogPrompt [27] | Kanıta dayalı açıklama, karar kuralda | Açıklama kalitesi ölçülmedi |

Literatürdeki çalışmaların çoğu ya tek bir bileşene odaklanır ya da büyük bulut sağlayıcılarının iç sistemlerine
dayanır. Bu proje, tek bir .NET ekosistemi içinde toplama, maskeleme, ayrıştırma, canlı anomali skoru, dağıtım duyarlı olay
ilişkilendirme, LLM açıklaması ve geliştirici geri bildirimini uçtan uca birleştirir. Öne çıkan özellikleri şunlardır:

- Her adım açık kanıtla gerekçelendirilir.
- Değerlendirme, literatürde dile getirilen bölme ve veri seti sorunlarını gözeterek yapılmıştır.

## Kaynaklar

[1] S. He, P. He, Z. Chen, T. Yang, Y. Su, M. R. Lyu, "A Survey on Automated Log Analysis for Reliability Engineering,"
*ACM Computing Surveys*, 54(6), Art. 130, 2021. doi:10.1145/3460345

[2] P. Notaro, J. Cardoso, M. Gerndt, "A Survey of AIOps Methods for Failure Management," *ACM Transactions on Intelligent
Systems and Technology*, 12(6), Art. 81, 2021. doi:10.1145/3483424

[3] M. Landauer, S. Onder, F. Skopik, M. Wurzenberger, "Deep Learning for Anomaly Detection in Log Data: A Survey,"
*Machine Learning with Applications*, 12, 100470, 2023. doi:10.1016/j.mlwa.2023.100470

[4] P. He, J. Zhu, Z. Zheng, M. R. Lyu, "Drain: An Online Log Parsing Approach with Fixed Depth Tree," *IEEE International
Conference on Web Services (ICWS)*, 2017.

[5] J. Zhu, S. He, J. Liu, P. He, Q. Xie, Z. Zheng, M. R. Lyu, "Tools and Benchmarks for Automated Log Parsing,"
*ICSE-SEIP*, 2019, pp. 121–130.

[6] Z. Jiang ve diğ., "A Large-Scale Evaluation for Log Parsing
Techniques: How Far Are We?," *ISSTA*, 2024. doi:10.1145/3650212.3652123

[7] V.-H. Le, H. Zhang, "Log Parsing with Prompt-based Few-shot Learning," *ICSE*, 2023, pp. 2438–2449.

[8] Z. Jiang ve diğ., "LILAC: Log Parsing using LLMs with Adaptive
Parsing Cache," *Proc. ACM Softw. Eng.*, 1(FSE), 2024, pp. 137–160. doi:10.1145/3643733

[9] W. Xu, L. Huang, A. Fox, D. Patterson, M. I. Jordan, "Detecting Large-Scale System Problems by Mining Console Logs,"
*SOSP*, 2009.

[10] S. He, J. Zhu, P. He, M. R. Lyu, "Experience Report: System Log Analysis for Anomaly Detection," *ISSRE*, 2016,
pp. 207–218. doi:10.1109/ISSRE.2016.21

[11] F. T. Liu, K. M. Ting, Z.-H. Zhou, "Isolation Forest," *IEEE International Conference on Data Mining (ICDM)*, 2008.

[12] M. Du, F. Li, G. Zheng, V. Srikumar, "DeepLog: Anomaly Detection and Diagnosis from System Logs through Deep Learning,"
*ACM CCS*, 2017.

[13] W. Meng ve diğ., "LogAnomaly: Unsupervised
Detection of Sequential and Quantitative Anomalies in Unstructured Logs," *IJCAI*, 2019, pp. 4739–4745.
doi:10.24963/ijcai.2019/658

[14] X. Zhang ve diğ., "Robust Log-Based Anomaly Detection on Unstable Log Data," *ESEC/FSE*, 2019, pp. 807–817.
doi:10.1145/3338906.3338931

[15] H. Guo, S. Yuan, X. Wu, "LogBERT: Log Anomaly Detection via BERT," *International Joint Conference on Neural Networks
(IJCNN)*, 2021. arXiv:2103.04475

[16] V.-H. Le, H. Zhang, "Log-based Anomaly Detection Without Log Parsing," *ASE*, 2021, pp. 492–504.
doi:10.1109/ASE51524.2021.9678773

[17] V.-H. Le, H. Zhang, "Log-based Anomaly Detection with Deep Learning: How Far Are We?," *ICSE*, 2022. arXiv:2202.04301

[18] M. Landauer, F. Skopik, M. Wurzenberger, "A Critical Review of Common Log Data Sets Used for Evaluation of
Sequence-based Anomaly Detection Techniques," *Proc. ACM Softw. Eng.*, 1(FSE), Art. 61, 2024, pp. 1354–1375.

[19] Q. Lin, H. Zhang, J.-G. Lou, Y. Zhang, X. Chen, "Log Clustering based Problem Identification for Online Service
Systems," *ICSE Companion (SEIP)*, 2016, pp. 102–111.

[20] S. He ve diğ., "Identifying Impactful Service System Problems via Log
Analysis," *ESEC/FSE*, 2018.

[21] Z. Chen ve diğ., "Towards Intelligent Incident Management: Why We Need It and How We Make It," *ESEC/FSE (Industry)*,
2020, pp. 1487–1497. doi:10.1145/3368089.3417055

[22] Y. Chen ve diğ., "Identifying Linked Incidents in Large-Scale Online Service Systems," *ESEC/FSE*, 2020, pp. 304–314.
doi:10.1145/3368089.3409768

[23] Z. Li ve diğ., "Gandalf: An Intelligent, End-To-End Analytics Service for Safe Deployment in Large-Scale Cloud
Infrastructure," *USENIX NSDI*, 2020, pp. 389–402.

[24] G. Yu ve diğ., "MicroRank: End-to-End Latency Issue Localization with Extended Spectrum Analysis in Microservice
Environments," *The Web Conference (WWW)*, 2021, pp. 3087–3098. doi:10.1145/3442381.3449905

[25] T. Ahmed, S. Ghosh, C. Bansal, T. Zimmermann, X. Zhang, S. Rajmohan, "Recommending Root-Cause and Mitigation Steps for
Cloud Incidents using Large Language Models," *ICSE*, 2023. arXiv:2301.03797

[26] Y. Chen ve diğ., "Automatic Root Cause Analysis via Large Language Models for Cloud Incidents," *EuroSys*, 2024.
doi:10.1145/3627703.3629553

[27] Y. Liu ve diğ., "Interpretable Online Log Analysis Using Large Language Models with Prompt Strategies," *ICPC*, 2024.
arXiv:2308.07610

[28] J. Zhu ve diğ., "Loghub: A Large Collection of System Log Datasets for AI-driven Log
Analytics," *ISSRE*, 2023.

[29] A. Oliner, J. Stearley, "What Supercomputers Say: A Study of Five System Logs," *IEEE/IFIP DSN*, 2007.
