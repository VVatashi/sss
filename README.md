# SimpleShadowsocks

Простой Shadowsocks-подобный стек на .NET 9: туннельный сервер, локальный SOCKS5-клиент и Android UI/VPN-клиент.

## Содержание

- [Для пользователя](#для-пользователя)
  - [Что это и когда использовать](#что-это-и-когда-использовать)
  - [Состав приложений](#состав-приложений)
  - [Готовые релизы и бинарники](#готовые-релизы-и-бинарники)
  - [Установка и настройка](#установка-и-настройка)
  - [Запуск](#запуск)
  - [Проверка работы](#проверка-работы)
- [Для разработчика](#для-разработчика)
  - [Технические требования](#технические-требования)
  - [Сборка из исходников](#сборка-из-исходников)
  - [Тестирование](#тестирование)
  - [Архитектура решения](#архитектура-решения)
  - [Логирование](#логирование)
  - [Детальный разбор протокола](#детальный-разбор-протокола)
  - [Ключевые архитектурные решения](#ключевые-архитектурные-решения)
  - [Ограничения текущей версии](#ограничения-текущей-версии)
  - [Следующие итерации](#следующие-итерации)

## Для пользователя

### Что это и когда использовать

SimpleShadowsocks позволяет поднять защищенный TCP/UDP-туннель между клиентом и сервером:

- на сервере запускается tunnel endpoint;
- на клиенте поднимается локальный SOCKS5-прокси (например `127.0.0.1:1080`);
- приложения направляют трафик в этот SOCKS5-прокси;
- данные идут к вашему серверу в зашифрованном виде.

Подходит для личного использования и тестовых окружений, где нужен простой управляемый туннель без сложной инфраструктуры.

### Состав приложений

В проект входят 3 пользовательских приложения:

1. `SimpleShadowsocks.Server` (CLI)
- Принимает туннельные соединения от клиентов.
- Разворачивается на VPS/сервере с публичным IP или DNS.

2. `SimpleShadowsocks.Client` (CLI)
- Поднимает локальный SOCKS5 (`CONNECT` + `UDP ASSOCIATE`).
- Подключается к `SimpleShadowsocks.Server`.

3. `SimpleShadowsocks.Client.Maui` (Android)
- UI-клиент для Android.
- Содержит режим VPN (`VpnService`), который маршрутизирует TCP/UDP/DNS через туннель.

### Готовые релизы и бинарники

Актуально на 16 марта 2026 года (данные из GitHub Releases):

- Последний релиз: [`v0.2.0`](https://github.com/VVatashi/sss/releases/tag/v0.2.0)
- Дата публикации: `2026-03-16 15:18:11 UTC`
- Описание релиза: `Add UDP support`
- Предыдущий релиз: [`v0.1.0`](https://github.com/VVatashi/sss/releases/tag/v0.1.0)

Доступные артефакты релиза `v0.2.0`:

- Сервер:
  - [`server-win-x64.zip`](https://github.com/VVatashi/sss/releases/download/v0.2.0/server-win-x64.zip)
  - [`server-linux-x64.zip`](https://github.com/VVatashi/sss/releases/download/v0.2.0/server-linux-x64.zip)
  - [`server-osx-x64.zip`](https://github.com/VVatashi/sss/releases/download/v0.2.0/server-osx-x64.zip)
  - [`server-osx-arm64.zip`](https://github.com/VVatashi/sss/releases/download/v0.2.0/server-osx-arm64.zip)
- Клиент CLI:
  - [`client-win-x64.zip`](https://github.com/VVatashi/sss/releases/download/v0.2.0/client-win-x64.zip)
  - [`client-linux-x64.zip`](https://github.com/VVatashi/sss/releases/download/v0.2.0/client-linux-x64.zip)
  - [`client-osx-x64.zip`](https://github.com/VVatashi/sss/releases/download/v0.2.0/client-osx-x64.zip)
  - [`client-osx-arm64.zip`](https://github.com/VVatashi/sss/releases/download/v0.2.0/client-osx-arm64.zip)
- Android:
  - [`com.simpleshadowsocks.client.maui-Signed.apk`](https://github.com/VVatashi/sss/releases/download/v0.2.0/com.simpleshadowsocks.client.maui-Signed.apk)

### Установка и настройка

#### 1) Сервер

1. Скачайте архив `server-<platform>.zip` из релиза.
2. Распакуйте на сервере.
3. Создайте/обновите `appsettings.json` рядом с `SimpleShadowsocks.Server.dll`:

```json
{
  "ListenPort": 8388,
  "SharedKey": "your-strong-key",
  "HandshakeMaxClockSkewSeconds": 60,
  "ReplayWindowSeconds": 300,
  "MaxConcurrentTunnels": 1024,
  "MaxSessionsPerTunnel": 1024,
  "ConnectTimeoutMs": 10000
}
```

`SharedKey` на клиенте и сервере должен быть одинаковым.

Поддерживаемые форматы `SharedKey`:

- `hex:<64 hex символа>` (32 байта)
- `base64:<...>` (ровно 32 байта после декодирования)
- обычная строка-пароль (из нее вычисляется `SHA-256`)

#### 2) Клиент CLI

1. Скачайте архив `client-<platform>.zip`.
2. Распакуйте локально.
3. Настройте `appsettings.json`:

```json
{
  "ListenPort": 1080,
  "RemoteHost": "your-server-host",
  "RemotePort": 8388,
  "RemoteServers": [],
  "SharedKey": "your-strong-key",
  "ProtocolVersion": 2,
  "EnableCompression": false,
  "CompressionAlgorithm": "Deflate",
  "TunnelCipherAlgorithm": "ChaCha20Poly1305"
}
```

Если используете несколько серверов, задайте `RemoteServers` списком. При непустом `RemoteServers` используется именно он (балансировка `round-robin`).

#### 3) Android клиент

1. Скачайте APK из релиза и установите на устройство.
2. При первом запуске заполните поля:
- `Local SOCKS5 Port`
- `Tunnel Host`
- `Tunnel Port`
- `Shared Key`
- `Cipher Algorithm`
- `Enable Compression` / `Compression Algorithm` (опционально)
3. Нажмите `Start`.
4. Подтвердите системный VPN-диалог Android (однократно для первого старта).

Важно:

- Для Android VPN MVP должен быть отключен `Private DNS`, иначе `DNS-over-TLS (:853)` не пойдет через текущую реализацию.

### Запуск

#### Сервер (CLI)

```powershell
dotnet SimpleShadowsocks.Server.dll 8388
```

Аргументы: `<listenPort> [sharedKey]`

#### Клиент (CLI)

```powershell
dotnet SimpleShadowsocks.Client.dll 1080 your-server-host 8388
```

Аргументы: `<listenPort> <remoteHost> <remotePort> [sharedKey]`

После запуска клиент слушает локальный SOCKS5 на `127.0.0.1:1080`.

#### Linux: запуск сервера как systemd unit

```ini
[Unit]
Description=SimpleShadowsocks Tunnel Server
After=network-online.target
Wants=network-online.target

[Service]
Type=simple
User=nobody
Group=nogroup
WorkingDirectory=/opt/simple-shadowsocks/server
ExecStart=/usr/bin/dotnet /opt/simple-shadowsocks/server/SimpleShadowsocks.Server.dll 8388
Restart=always
RestartSec=2
Environment=DOTNET_ENVIRONMENT=Production
LimitNOFILE=65535

[Install]
WantedBy=multi-user.target
```

### Проверка работы

1. Запустите сервер.
2. Запустите клиент.
3. В приложении/браузере укажите SOCKS5-прокси `127.0.0.1:1080`.
4. Проверьте, что трафик проходит через туннель.

## Для разработчика

### Технические требования

- `.NET SDK 9.0+`
- Зафиксированная версия SDK в `global.json`: `9.0.312`
- На уровне решения сборка ограничена в один поток (`Directory.Build.rsp`, `Directory.Build.props`), чтобы исключить конфликты артефактов
- Для MAUI Android: workload `maui-android` (локальную MAUI/Android сборку в этом проекте рекомендуется выполнять только на подготовленном окружении)

Команды запускать из корня репозитория.

### Сборка из исходников

Рекомендуемая последовательность (сначала общие DLL, затем приложения по одному):

```powershell
# Библиотеки
dotnet build src\SimpleShadowsocks.Protocol\SimpleShadowsocks.Protocol.csproj
dotnet build src\SimpleShadowsocks.Client.Core\SimpleShadowsocks.Client.Core.csproj
dotnet build src\SimpleShadowsocks.Server.Core\SimpleShadowsocks.Server.Core.csproj

# Приложения
dotnet build src\SimpleShadowsocks.Client\SimpleShadowsocks.Client.csproj
dotnet build src\SimpleShadowsocks.Server\SimpleShadowsocks.Server.csproj

# Тестовый проект
dotnet build tests\SimpleShadowsocks.Client.Tests\SimpleShadowsocks.Client.Tests.csproj
```

Публикация Release-бинарников (framework-dependent):

```powershell
# Windows x64
dotnet publish src\SimpleShadowsocks.Server\SimpleShadowsocks.Server.csproj -c Release -r win-x64 --self-contained false
dotnet publish src\SimpleShadowsocks.Client\SimpleShadowsocks.Client.csproj -c Release -r win-x64 --self-contained false

# Linux x64
dotnet publish src\SimpleShadowsocks.Server\SimpleShadowsocks.Server.csproj -c Release -r linux-x64 --self-contained false
dotnet publish src\SimpleShadowsocks.Client\SimpleShadowsocks.Client.csproj -c Release -r linux-x64 --self-contained false

# macOS x64
dotnet publish src\SimpleShadowsocks.Server\SimpleShadowsocks.Server.csproj -c Release -r osx-x64 --self-contained false
dotnet publish src\SimpleShadowsocks.Client\SimpleShadowsocks.Client.csproj -c Release -r osx-x64 --self-contained false

# macOS ARM64
dotnet publish src\SimpleShadowsocks.Server\SimpleShadowsocks.Server.csproj -c Release -r osx-arm64 --self-contained false
dotnet publish src\SimpleShadowsocks.Client\SimpleShadowsocks.Client.csproj -c Release -r osx-arm64 --self-contained false
```

MAUI/Android publish рекомендуется выполнять в отдельном подготовленном окружении (CI или выделенная Android build-машина), а не в обычном локальном контуре разработки.

```powershell
dotnet publish src\SimpleShadowsocks.Client.Maui\SimpleShadowsocks.Client.Maui.csproj -c Release -f net9.0-android
```

Результаты публикации:

- `bin/SimpleShadowsocks.Server/Release/net9.0/<RID>/publish`
- `bin/SimpleShadowsocks.Client/Release/net9.0/<RID>/publish`

### Тестирование

Базовый прогон:

```powershell
dotnet test tests\SimpleShadowsocks.Client.Tests\SimpleShadowsocks.Client.Tests.csproj
```

Категории:

- `Unit`
- `Integration`
- `Performance`

Ключевые сценарии, покрытые тестами:

- TCP: SOCKS5 `CONNECT` (standalone + через tunnel), relay данных, классифицированные коды ошибок upstream на сервере, failover на следующий tunnel-сервер при отказе `CONNECT`.
- UDP: SOCKS5 `UDP ASSOCIATE` работает только через tunnel и UDP relay на сервере; relay датаграмм до IP и domain (`localhost`), поддержка и тесты реассамблинга фрагментов `FRAG`.
- Performance: отдельный UDP benchmark-тест для `UDP ASSOCIATE` через tunnel (throughput + packets/sec).
- Наблюдаемость: при `UDP ASSOCIATE` без tunnel backend пишется явный лог `UDP disabled: no tunnel backend` и инкрементируется .NET-метрика `socks5_udp_associate_rejected_no_tunnel_backend_total`.

Скрипт последовательного запуска:

```powershell
pwsh .\tests\run-tests.ps1
```

По умолчанию запускаются только `Unit` + `Integration`; performance-тесты включаются только явно:

```powershell
pwsh .\tests\run-tests.ps1 -IncludePerformance
```

Ручной запуск по категориям:

```powershell
dotnet test tests\SimpleShadowsocks.Client.Tests\SimpleShadowsocks.Client.Tests.csproj --filter "Category=Unit"
dotnet test tests\SimpleShadowsocks.Client.Tests\SimpleShadowsocks.Client.Tests.csproj --filter "Category=Integration"
dotnet test tests\SimpleShadowsocks.Client.Tests\SimpleShadowsocks.Client.Tests.csproj --filter "Category=Performance"
```

Полезные переменные окружения для performance-тестов (стабилизация под слабые/загруженные машины):

- `SS_PERF_TOTAL_MB` (по умолчанию `128`)
- `SS_PERF_CHUNK_KB` (по умолчанию `64`)
- `SS_PERF_STREAMS` (по умолчанию `4`)
- `SS_PERF_PAYLOAD_VARIANTS` (по умолчанию `256`)
- `SS_PERF_WARMUP_TIMEOUT_SEC` (по умолчанию `90`)
- `SS_PERF_MEASUREMENT_TIMEOUT_SEC` (по умолчанию `600`)

### Архитектура решения

Слои решения:

1. `SimpleShadowsocks.Protocol`
- Wire-модели, framing/serialization, crypto-handshake, AEAD-шифрование, compression codecs.

2. `SimpleShadowsocks.Client.Core`
- SOCKS5 сервер (`CONNECT`, `UDP ASSOCIATE`), мультиплексированный tunnel client, reconnect/heartbeat/session restore.

3. `SimpleShadowsocks.Server.Core`
- Tunnel server: handshake, диспетчеризация кадров, управление сессиями, проксирование в upstream TCP/UDP.

4. Тонкие хосты:
- `SimpleShadowsocks.Client` (CLI launcher)
- `SimpleShadowsocks.Server` (CLI launcher)
- `SimpleShadowsocks.Client.Maui` (Android UI/VPN launcher)

Ключевые директории:

- `src/SimpleShadowsocks.Protocol`
- `src/SimpleShadowsocks.Client.Core/Socks5`
- `src/SimpleShadowsocks.Client.Core/Tunnel`
- `src/SimpleShadowsocks.Server.Core/Tunnel`
- `tests/SimpleShadowsocks.Client.Tests`

### Логирование

Логирование унифицировано по формату для всех runtime-компонентов (`Client`, `Server`, `Client.Core`, `Server.Core`):

- timestamp в UTC (`ISO-8601`, поле в начале строки);
- уровень (`level`);
- компонент (`component`);
- протокол (`protocol`);
- идентификатор сессии (`session`, `-` если не применимо);
- сообщение (`msg`) и поля ошибки (`error_type`, `error`) при исключениях.

Пример:

```text
2026-03-16T19:10:42.1234567+00:00 level=INFO component=socks5-server protocol=TUNNEL/UDP session=42 msg="tunnel udp session opened"
```

Политика:

- логируются подключения/соединения (start/listen/accept/open/close);
- логируются ошибки/сбои;
- отсутствует логирование в hot-path на каждый пакет данных.

### Детальный разбор протокола

Поддерживаются две версии framing.

`v1`:

```text
+--------+----------+------------+-------------+-----------+--------------+
| VER(1) | TYPE(1)  | SESSION(4) | SEQUENCE(8) | LEN(4)    | PAYLOAD(N)   |
+--------+----------+------------+-------------+-----------+--------------+
```

`v2`:

```text
+--------+----------+----------+------------+-------------+-----------+--------------+
| VER(1) | TYPE(1)  | FLAGS(1) | SESSION(4) | SEQUENCE(8) | LEN(4)    | PAYLOAD(N)   |
+--------+----------+----------+------------+-------------+-----------+--------------+
```

Поля:

- `VER`: версия кадра (`1` или `2`)
- `TYPE`: `Connect(1)`, `Data(2)`, `Close(3)`, `Ping(4)`, `Pong(5)`, `UdpAssociate(6)`, `UdpData(7)`
- `FLAGS` (только `v2`):
  - `0x01` `PayloadCompressed`
  - `0x02` `CompressionEnabled`
  - `0x0C` `CompressionAlgorithmMask` (`Deflate`, `Gzip`, `Brotli`)
- `SESSION`: идентификатор логической сессии (`0` зарезервирован для control-frame)
- `SEQUENCE`: монотонный счетчик кадров в рамках сессии
- `LEN`: длина payload

Правила:

- максимум `PAYLOAD`: `1 MiB` (`ProtocolConstants.MaxPayloadLength`)
- сервер принимает `v1` и `v2`; версия фиксируется первым кадром
- клиент валидирует версию ответов и закрывает туннель при несовпадении
- для `v2` алгоритм компрессии кодируется во `FLAGS`
- при нарушении monotonic `SEQUENCE` сессия закрывается (ordering/replay policy)

Crypto-handshake:

- версия `v2`, магии `TSC2`/`TSS2`
- pre-shared key + HMAC + timestamp + handshake counter
- защита от replay через bounded cache и replay window
- клиент выбирает `TunnelCipherAlgorithm`, сервер подтверждает поддерживаемый алгоритм
- HKDF разделяет материал на MAC-key и transport-key
- после handshake все кадры идут через AEAD duplex stream
- nonce policy: base nonce + counter, с защитой от overflow

Поддерживаемые AEAD:

- `ChaCha20-Poly1305`
- `AES-256-GCM`
- `AEGIS-128L`
- `AEGIS-256`

### Ключевые архитектурные решения

1. Multiplexing нескольких session в одном TCP-туннеле
- снижает overhead на соединения и упрощает управление reconnect.

2. Sticky-привязка SOCKS5 TCP-сессии к выбранному tunnel-серверу + failover на CONNECT
- при успешном `CONNECT` сессия закрепляется за выбранным сервером;
- если конкретный сервер вернул ошибку на `CONNECT`, клиент пробует следующий сервер из списка (`round-robin`-порядок).

3. Reconnect + session migration/resume
- при разрыве туннеля клиент пытается прозрачно восстановить активные session.

4. Неблокирующий `CONNECT` на сервере
- медленные upstream-коннекты не должны блокировать read-loop всего туннеля.

5. Вынесенный protocol/crypto слой
- `Protocol` изолирован от хостов и может эволюционировать отдельно.

6. Опциональная компрессия только в `v2`
- позволяет сохранять совместимость с `v1` и гибко выбирать профиль трафика.

### Ограничения текущей версии

- Нет ротации pre-shared key.
- Нет selective recovery/retransmit при sequence-ошибках.
- Session resume best-effort: конкретная сессия может завершиться при неуспешном re-`CONNECT`.
- Android VPN MVP не поддерживает режим `Private DNS`/DoT (`:853`) через текущий pipeline.

### Следующие итерации

1. Добавить ротацию ключей и key-update процедуру.
2. Добавить метрики по sequence violations, reconnect и session-failures.
3. Добавить persist/distributed replay-cache для multi-instance серверов.
4. Ввести отдельные timeout/deadline policy для session-resume.
