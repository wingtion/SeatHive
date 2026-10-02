# SeatHive backend denetim raporu

## Bağlam

Hedef, SeatHive'ı canlı bir portföy demosuna çevirmek (PRODUCT.md). Yeni backend işlerine başlamadan önce mevcut kod salt-okunur olarak denetlendi; hiçbir dosya değiştirilmedi. Bu belge bir uygulama planı değil, istenen rapordur.

Kapsam: takip edilen 38 dosyanın tamamı okundu. **Yapılmayanlar:** derleme ve testler çalıştırılmadı, simülasyon çalıştırılmadı, paketlerin son sürümleri NuGet'ten sorgulanmadı (bölüm 4'teki "eski" değerlendirmeleri bilgime dayanıyor; `dotnet list package --outdated` ile doğrulanmalı).

## En önemli sonuç

Projenin ana iddiası olan "20 istekten yalnızca 1'i başarılı olur" kodda garanti değil. Kilidi alamayan istek, kilidi alanın kilidini siliyor (K1), veritabanında da ikinci bir koruma yok (K2). İkisi birlikte gerçek bir çifte rezervasyon yolu açıyor. Ayrıca repo bu haliyle `docker-compose up --build` ile ayağa kalkmıyor (K3) ve herkese açık bir sunucuya konursa veritabanı anonim bir istekle silinebiliyor (K4).

## Bulgular (kritikten düşüğe)

### Kritik

**K1. Kilidi alamayan istek başkasının kilidini siliyor** — kilit / yarış koşulu
- Yer: `SeatHive.Api/Services/BookingService.cs:25-28, 52-55`
- Sorun: `AcquireLockAsync` `try` bloğunun içinde. Kilit alınamayınca `return "System busy."` çalışıyor, ardından `finally` yine de `ReleaseLockAsync(lockKey)` çağırıyor ve o an kilidi tutan isteğin anahtarını siliyor.
- Neden önemli: A kilidi alır ve koltuğu okur. B alamaz, A'nın kilidini siler. C kilidi alır, A henüz kaydetmediği için koltuğu boş görür. A ve C ikisi de kaydeder, ikisi de "Booking successful!" döner, ikisi de event yayınlar. Simülasyonda 19 kaybeden olduğu için bu yol gerçekten erişilebilir; sonuç zamanlamaya bağlı.
- Çözüm: `Acquire`'ı `try` dışına al; yalnızca kilit alındıysa bırak. K2 ile birlikte düzelt.

**K2. Kilit sahipliği yok** — kilit güvenliği
- Yer: `SeatHive.Api/Services/RedisLockService.cs:19, 22-26`
- Sorun: Değer sabit `"locked"`; bırakma koşulsuz `DEL`. Kimin kilidi olduğu bilinmiyor.
- Neden önemli: K1'in kök nedeni. Ayrıca TTL dolduktan sonra geç biten istek, sonradan gelenin kilidini siler (bkz. Y1).
- Çözüm: Her alışta rastgele token (GUID) yaz; bırakmayı Lua ile "değer benim token'ımsa sil" yap. Arayüz token/handle döndürsün (`IAsyncDisposable` uygun).

**K3. Veritabanı seviyesinde ikinci koruma yok** — veri tutarlılığı
- Yer: `SeatHive.Api/Models/Seat.cs` (`Version`), `SeatHive.Api/Data/AppDbContext.cs:6-15`, `BookingService.cs:30-39`
- Sorun: `Version` sıradan bir `int`; concurrency token olarak işaretlenmemiş, `OnModelCreating` yok. Oku-kontrol et-yaz akışı koşulsuz `UPDATE` üretiyor; son yazan kazanır.
- Neden önemli: Redis kilidi herhangi bir nedenle delinirse (K1, Y1, Redis yeniden başlaması) çifte rezervasyonu hiçbir şey durdurmuyor.
- Çözüm: Koşullu güncelleme (`ExecuteUpdateAsync ... WHERE Id = @id AND IsBooked = false`, etkilenen satır 0 ise reddet) veya Npgsql `xmin` concurrency token + `DbUpdateConcurrencyException` yakalama. Rezervasyon ayrı tabloya taşınırsa `SeatId` üzerinde unique index.

**K4. Repo bu haliyle Docker ile çalışmıyor**
- Yer: `.gitignore:8` (`Dockerfile`), `docker-compose.yml:9, 27`, `SeatHive.Worker/Program.cs:13`
- Sorun: Compose `SeatHive.Api/Dockerfile` ve `SeatHive.Worker/Dockerfile` bekliyor; ikisi de gitignore'da ve çalışma dizininde de yok. Worker ayrıca RabbitMQ adresini `"localhost"` olarak sabitlemiş; compose'un verdiği `RabbitMQ__HostName` okunmuyor.
- Neden önemli: README'deki tek çalıştırma yolu temiz klonda başarısız. Dockerfile eklense bile worker konteyner içinde RabbitMQ'ya bağlanamaz, event zinciri çalışmaz.
- Çözüm: İki Dockerfile'ı yaz ve takibe al, `.gitignore`'dan satırı kaldır. Worker'da host'u API'deki gibi konfigürasyondan oku.

**K5. Anonim istek veritabanını silebiliyor**
- Yer: `SeatHive.Api/Controllers/SetupController.cs:8-9, 20-25`
- Sorun: `[Authorize]` yok; `EnsureDeleted()` tüm veritabanını (kullanıcılar dahil) düşürüyor.
- Neden önemli: Canlı demoda herkes tek istekle sistemi sıfırlar. Açık bağlantılar varken `EnsureDeleted` hata da verebilir.
- Çözüm: Admin rolüne bağla; veritabanını düşürmek yerine demo verisini sıfırlayan (koltukları boşaltan) bir işlem yap; rate limit ekle.

**K6. Sırlar repoda ve altyapı portları dışarı açık**
- Yer: `SeatHive.Api/appsettings.json:10, 15`; `docker-compose.yml:11, 30, 41, 43-44, 51-52, 59-61, 63-64`; `SeatHive.Api/Program.cs:25-26`; `SeatHive.Worker/Program.cs:15-16`
- Sorun: JWT imza anahtarı ve Postgres parolası (`password`) commit edilmiş; RabbitMQ `guest/guest` kodda sabit. Compose, Postgres, parolasız Redis ve RabbitMQ portlarını host'a yayınlıyor.
- Neden önemli: Anahtarı bilen herkes istediği kullanıcı (ileride admin) için token üretir. Aynı compose sunucuda çalışırsa veritabanı ve Redis internete açılır. Yalnızca yerelde kalırsa önem derecesi düşer; canlı demo hedefi için kritik.
- Çözüm: Sırları ortam değişkeni / user-secrets'a taşı, anahtarı döndür (git geçmişinde kalacak), üretim compose'unda altyapı portlarını yayınlama, Redis'e parola ver.

### Yüksek

**Y1. TTL dolarken iş bitmezse korumasız kalınıyor**
- Yer: `BookingService.cs:27` (10 sn), `30-48`
- Sorun: Yenileme (watchdog) ve fencing yok. DB veya RabbitMQ 10 saniyeden uzun sürerse kilit düşer, ikinci istek girer; ilk istek bitince ikincinin kilidini siler.
- Çözüm: K2 + K3 bunu zararsız hale getirir. Kilidi yalnızca DB yazımı boyunca tut; publish'i kilit dışına çıkar.

**Y2. DB kaydı ile event yayını atomik değil**
- Yer: `BookingService.cs:39-48`
- Sorun: `SaveChanges` başarılı olup `Publish` hata verirse koltuk dolu kalır, kullanıcı 500 alır, event hiç gitmez.
- Neden önemli: Planlanan booking → payment → notification zaman çizelgesi bu event'e dayanıyor.
- Çözüm: MassTransit EF Core transactional outbox.

**Y3. Simülasyon endpoint'i anonim, limitsiz ve gerçek yolu atlıyor**
- Yer: `SeatHive.Api/Controllers/SimulationController.cs:8-9, 18-41`
- Sorun: Yetki ve rate limit yok; koltuk #1 ve 20 kullanıcı sabit; HTTP ve JWT katmanından geçmiyor; var olmayan kullanıcı kimlikleri (1001–1020) ile gerçek koltuğu kalıcı olarak rezerve ediyor. İkinci çalıştırmada 0 başarı döner ve yine "System is safe." der (`:53-55`).
- Çözüm: Bölüm 7'ye bakın.

**Y4. Şema hiç migrate edilmiyor**
- Yer: `SeatHive.Api/Program.cs` (`Migrate()` yok), `SetupController.cs:21`, `Migrations/20260215132940_AddUsersTable.cs`
- Sorun: Tablolar yalnızca `create-data` çağrılınca `EnsureCreated` ile oluşuyor; o zamana kadar register/login hata verir. `EnsureCreated` migration geçmişi yazmadığı için migration'lar fiilen ölü ve sonradan `Migrate()` ile çakışır.
- Çözüm: Başlangıçta `Database.MigrateAsync()`; `EnsureCreated/EnsureDeleted` kullanımını kaldır.

**Y5. Test kapsamı çekirdek iddiayı doğrulamıyor**
- Yer: `SeatHive.Tests/BookingServiceTests.cs:32-33, 39-67`
- Sorun: Yalnızca 2 test var; kilit mock'u her zaman `true` dönüyor. K1'i yakalayacak test yok. InMemory sağlayıcı concurrency token ve kısıtları uygulamaz.
- Çözüm: Bölüm 3'e bakın.

**Y6. .NET 8 desteği 10 Kasım 2026'da bitiyor**
- Yer: dört `.csproj` dosyasında `net8.0`
- Sorun: Bugünden yaklaşık beş hafta sonra güvenlik yaması kesiliyor. Makinede yalnızca SDK 10.0.401 kurulu.
- Çözüm: Yeni işlere başlamadan `net10.0`'a (LTS, Kasım 2028'e kadar) geç.

### Orta

**O1. Girdi doğrulaması yok** — `Controllers/AuthController.cs:33-37`, `Models/BookingRequest.cs`
`Email`/`Password` için `[Required]`, `[EmailAddress]`, uzunluk sınırı yok; boş parola kabul ediliyor, BCrypt 72 bayttan sonrasını yok sayıyor. `BookingRequest.UserId` istemciden bağlanabiliyor (üzerine yazılıyor ama DTO'da olmamalı). Çözüm: ayrı istek DTO'ları ve data annotation / FluentValidation.

**O2. `Users.Email` üzerinde unique index yok** — `Services/AuthService.cs:25-36`, migration `:29-41`
Kontrol-sonra-ekle yarışı aynı e-postayla iki kullanıcı yaratabilir; büyük/küçük harf de normalize edilmiyor. Çözüm: unique index, e-postayı küçük harfe çevir, `DbUpdateException` yakala.

**O3. Login/register üzerinde rate limit yok** — `Program.cs`
Parola deneme ve BCrypt ile CPU tüketme saldırısına açık. Çözüm: yerleşik `AddRateLimiter` (zaten planlı).

**O4. Sonuçlar sihirli string** — `BookingService.cs:28-50`, `BookingController.cs:37-40`, `AuthController.cs:22`, `SimulationController.cs:45-46`
Controller'lar `"Booking successful!"` metnini karşılaştırıyor; tüm hatalar 400. "Koltuk dolu" 409, "bulunamadı" 404 olmalı. `"System busy."` aslında "başka biri şu an bu koltuğu alıyor" demek. Çözüm: enum/result tipi ve doğru HTTP kodları. Arayüz bu ayrımı göstermek zorunda.

**O5. Veri modeli planlanan akışı taşımıyor** — `Models/Seat.cs`, migration `:43-71`
Rezervasyon bir koltuk satırındaki bayrak; `Seat.UserId` için FK yok; hold/ödeme durumu, zaman damgası yok; `(EventId, Section, Row, SeatNumber)` unique değil.

**O6. Compose `ASPNETCORE_ENVIRONMENT=Development` ile çalışıyor** — `docker-compose.yml:14`, `Program.cs:95-99`
Swagger yalnızca bu yüzden açılıyor; canlıda geliştirici hata sayfası ayrıntı sızdırır.

**O7. MassTransit sürümleri uyumsuz** — `SeatHive.Api.csproj:13` (8.5.8), `SeatHive.Worker.csproj:12` (8.1.1)
Aynı mesaj sözleşmesini paylaşan iki servis aynı sürümde olmalı. Not: MassTransit 9 ticari lisansa geçti; 8.x'te kalmak bilinçli bir karar olmalı.

**O8. Redis bağlantısı başlangıçta senkron ve hata yönetimsiz** — `Program.cs:36-40`
`AbortOnConnectFail=false` ile uygulama Redis olmadan açılır; her rezervasyon isteği işlenmemiş istisnayla 500 döner (`finally` içindeki `DEL` de atar).

**O9. Yerel bağlantı portu tutmuyor** — `appsettings.json:10` (5433) ve `docker-compose.yml:44` (5432)
Compose altyapısıyla API'yi yerelde çalıştırmak bağlanamaz.

### Düşük

- **D1. Ölü kod:** `Controllers/WeatherForecastController.cs`, `WeatherForecast.cs`, `SeatHive.Api.http` (şablon artığı); `SeatHive.Worker/Worker.cs` (hiç kaydedilmiyor); `Seat.Version` (artırılıyor, okunmuyor); worker'a verilen kullanılmayan `ConnectionStrings` (`docker-compose.yml:30`); migration adı `AddUsersTable` ama tüm şemayı kuruyor.
- **D2. Claim okuma kırılgan:** token `sub` yazıyor (`AuthService.cs:64`), controller `ClaimTypes.NameIdentifier` okuyor (`BookingController.cs:25`); yalnızca varsayılan claim eşlemesi sayesinde çalışıyor. `int.Parse` (`:34`) hatalı değerde 500 verir.
- **D3. `LoginRequest`** controller dosyasında tanımlı ve register için de kullanılıyor (`AuthController.cs:33`).
- **D4. Controller içinde senkron veri erişimi:** `SetupController.cs:21-48` (`SaveChanges`, `EnsureCreated`).
- **D5. Servisler somut sınıf olarak enjekte ediliyor** (`Program.cs:74, 76`); konfigürasyon `IConfiguration["Jwt:Key"]!` ile dağınık okunuyor (`Program.cs:87-89`, `AuthService.cs:59, 70-71`). Options pattern yeterli; her servise arayüz eklemek gereksiz karmaşıklık olur.
- **D6. Swagger güvenlik şeması `ApiKey`** (`Program.cs:54`); `Http` + `bearer` olursa "Bearer " yazmak gerekmez.
- **D7. `UseHttpsRedirection`** (`Program.cs:101`) konteynerde HTTPS portu olmadan etkisiz; ters vekil arkasında forwarded headers gerekir. Adım 5d'de eklendi: `ReverseProxy:TrustedProxies` ile yalnızca yapılandırılmış proxy'lerden gelen `X-Forwarded-For`/`X-Forwarded-Proto` kabul ediliyor (`ForwardLimit = 1`); `ForwardedHeadersTests` doğruluyor. Ters vekilin kendisi (Caddy) dağıtım adımında.
- **D8. Kullanıcı sayımı:** register "User already exists." dönüyor (`AuthService.cs:26`). Demo için kabul edilebilir.
- **D9. Loglarda string interpolasyonu:** `Worker/Consumers/BookingConsumer.cs:19, 24`; yapılandırılmış log şablonu kullanılmalı.
- **D10. İmaj ve paket yaşı:** `redis:alpine` etiketsiz, `rabbitmq:3-management` (3.x topluluk desteği bitti), compose `version:` anahtarı artık geçersiz; `Swashbuckle 6.6.2`, `xunit 2.5.3`, `Microsoft.NET.Test.Sdk 17.8.0`, `coverlet 6.0.0` eski; EF paketleri karışık (`8.0.11` ve `8.0.24`).

## 3. Test durumu ve eksik kritik testler

Mevcut: iki birim test (dolu koltuk reddi, boş koltuk başarısı). `AuthService`, controller'lar, `RedisLockService` ve consumer için test yok; CI ve kapsam eşiği yok.

Eksikler, öncelik sırasıyla:
1. Kilit alınamadığında `ReleaseLockAsync` **çağrılmamalı** (K1'i bugün kırmızıya düşürür).
2. Gerçek Postgres + Redis (Testcontainers) ile N eşzamanlı istek, tam 1 başarı; tekrar tekrar çalıştırılan.
3. `RedisLockService`: yanlış token ile bırakma kilidi silmemeli; TTL dolunca yeniden alınabilmeli.
4. DB koruması: kilit devre dışıyken bile ikinci yazım reddedilmeli.
5. Publish hata verince durum tutarlı kalmalı (outbox sonrası).
6. Koltuk bulunamadı; başarısız yollarda event yayınlanmamalı; istisnada kilit bırakılmalı.
7. `AuthService`: yinelenen e-posta, yanlış parola, token claim'leri ve rol.
8. Yetkilendirme: anonim ve admin olmayan kullanıcı reset/simülasyon çağıramamalı.

## 6. README ile kod arasındaki uyumsuzluklar

| README iddiası | Kod |
|---|---|
| Redlock algoritması | Tek Redis örneğinde `SET NX`, sahiplik kontrolü yok |
| Zero-Trust, RBAC | Rol yok; setup ve simülasyon anonim |
| "Only 1 succeeds, 19 rejected" | K1 + K3 nedeniyle garanti değil; simülasyon HTTP/JWT'den geçmiyor |
| Worker e-posta/PDF üretir | `Task.Delay(2000)` ve iki log satırı |
| Yüksek test kapsamı | 2 test |
| Tamamen Dockerize, `docker-compose up -d --build` | Dockerfile'lar repoda yok; worker `localhost`'a bağlanıyor |
| EF Core Code-First | Migration var ama hiç uygulanmıyor |
| Data Consistency | Outbox yok, DB koruması yok |
| `git clone .../YOUR_USERNAME/...` | Yer tutucu duruyor |

## 7. Planlanan işler için: ne değişmeli, ne kalabilir

**Mutlaka değişmeli**
- **Kilit servisi** (`IRedisLockService`, `RedisLockService`): token'lı alma, Lua ile bırakma. Koltuk tutma (hold) kısa ömürlü mutex'ten ayrı bir kavram: sahibi (kullanıcı/hold kimliği) değerde duran, dakikalar süren TTL'li anahtar; geri sayım için kalan süre okunabilmeli.
- **Veri modeli ve `AppDbContext`**: koltuk durumu (boş/tutuluyor/satıldı) veya ayrı `Booking` tablosu, concurrency token, unique index'ler, `Seat.UserId` FK, `User.Role`. Migration'lar baştan düzenlenmeli.
- **`BookingService`**: tutma, onaylama, bırakma olarak ayrılmalı; result tipi dönmeli; onay koşullu DB güncellemesiyle yapılmalı.
- **Mesajlaşma**: `BookingCreatedEvent` tek başına yetmez; hold, ödeme sonucu, onay ve bildirim event'leri ile outbox gerekir. Worker'ın ürettiği sonuçların SignalR'a ulaşması için API tarafında da consumer olmalı.
- **`Program.cs`**: CORS (SignalR için belirli origin + credentials), rate limiter, SignalR ve hub için query string'den JWT okuma, rol politikaları, başlangıçta migrate, sırların ortamdan okunması.
- **`SetupController`**: admin korumalı, veritabanını düşürmeyen reset; admin kullanıcı seed'i.
- **`SimulationController`**: parametreli, rate limit'li, gerçek rezervasyon yolundan geçen, her denemenin sonucunu arayüze event olarak yayan ve arkasını temizleyen bir yapı.
- **`AuthService`**: rol claim'i.
- **Dağıtım**: Dockerfile'lar, üretim compose'u, Netlify'dan (HTTPS) erişim için API önünde TLS sonlandıran ters vekil; aksi halde tarayıcı karışık içerik nedeniyle istekleri engeller.
- **Testler**: bölüm 3.

**Olduğu gibi kalabilir**
- Çözüm yapısı (Api / Worker / Shared / Tests).
- BCrypt ile parola hash'leme ve JWT doğrulama parametreleri (issuer, audience, süre, imza).
- Kullanıcı kimliğini token'dan alıp istek gövdesini ezme yaklaşımı (`BookingController.cs:34-35`).
- MassTransit + RabbitMQ kurulumu ve `ConfigureEndpoints` deseni.
- `Event` ve `User` modellerinin temeli, Swagger kurulumu, xUnit + Moq.

## Önerilen sıra (onaylanırsa)

1. K1, K2, K3 ve bunları kanıtlayan testler.
2. K4, Y4: temiz klondan tek komutla ayağa kalkma.
3. `net10.0` ve paket güncellemesi.
4. K5, K6, O1–O3: güvenlik ve RBAC.
5. Ardından yeni özellikler: veri modeli, hold, ödeme, event'ler, SignalR, simülasyon.
6. README'yi kodun gerçekten yaptığına göre düzelt.

Doğrulama: her adımda `dotnet test`; 1. adım için Testcontainers ile eşzamanlılık testinin art arda çalıştırılması; 2. adım için temiz klonda `docker compose up --build` ve Swagger üzerinden register → login → rezervasyon → worker logunda event.

## Ertelenen yükseltmeler

net10.0 geçişinde aşağıdaki üç paket bilerek en güncel büyük sürüme çıkarılmadı. O adımın kuralı davranış değişikliği ve refactor yapmamaktı; üçü de bu kuralı zorluyordu.

| Paket | Kullanılan | Ertelenen | Neden |
|---|---|---|---|
| Swashbuckle.AspNetCore | 9.0.6 | 10.2.3 | 10.x Microsoft.OpenApi 2'ye geçiyor ve `SeatHive.Api/Program.cs:46-72`'deki `Microsoft.OpenApi.Models` kullanımını kırıyor; Swagger kurulumunun yeniden yazılması gerekir. 9.x'in net10 hedefi yok, net9 derlemesiyle çalışıyor. |
| StackExchange.Redis | 2.13.17 | 3.3.1 | Büyük sürüm ve doğrudan kilit koduna (`RedisLockService`) dokunuyor; API uyumluluğu doğrulanmadı. |
| xunit.runner.visualstudio | 3.1.5 | 4.0.0 | 4.0.0'ın xunit 2.9.3 ile uyumu doğrulanmadı; 3.x xunit 2.x testlerini çalıştırıyor. |

Sıra:

- **StackExchange.Redis 3.x:** kilit testleri (bölüm 3, madde 1–4) yazıldıktan sonra yapılacak. Böylece K1–K3 düzeltmeleri bilinen bir istemci sürümünde doğrulanır ve yükseltmenin kilit davranışını bozup bozmadığı testlerle görülür.
- **Swashbuckle 10.x:** yapıldı (adım 5d, alt adım A): 10.2.3'e yükseltildi, Swagger kurulumu Microsoft.OpenApi 2'ye göre yeniden yazıldı. D6 da kapandı: güvenlik şeması `http`/`bearer`, ve yalnızca token isteyen endpoint'lere uygulanıyor (`AuthorizeOperationFilter`). `SwaggerTests` bunu ve Swagger'ın yalnızca Development'ta açık olduğunu doğruluyor.
- **xunit.runner.visualstudio 4.x:** xunit v3'e geçiş değerlendirilirken, test altyapısı işleriyle birlikte.
