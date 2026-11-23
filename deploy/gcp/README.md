# LabPet GCP 실시간 서버 (`LabPet.Server`) 배포 및 운영 가이드

본 문서는 이미 생성된 **GCP Compute Engine VM (Ubuntu 24.04 LTS, `e2-micro` 등)** 에 `LabPet.Server`(.NET 10 ASP.NET Core SignalR)와 Caddy HTTPS/WSS 리버스 프록시를 배포하고 운영하는 절차를 설명합니다.

> [!IMPORTANT]
> - 채팅 메시지 본문·빠른 반응·게임 이벤트는 서버 디스크나 별도 DB(Firestore/Cloud SQL 등)에 저장하지 않습니다.
> - 오프라인 친구에게 보낸 메시지는 즉시 전송 불가를 반환하며, 온라인 친구에게만 E2EE(AES-256-GCM + HMAC-SHA256) 봉투를 실시간 중계합니다.
> - `/var/lib/labpet/registry.json`에는 **기기 공개키 바인딩(`PetId ↔ PublicKey`)과 승인된 친구 관계**만 최소 저장됩니다.

---

## 1. 로컬 PC에서 Linux x64 서버 게시

```powershell
# 저장소 루트에서 실행
dotnet publish src\LabPet.Server\LabPet.Server.csproj -c Release -r linux-x64 --no-self-contained -o .\publish\labpet-server-linux-x64
```

> self-contained가 아닌 `--no-self-contained`로 게시해 업로드 크기를 최소화하고, VM에 설치된 `.NET 10 ASP.NET Core Runtime`을 사용합니다.

---

## 2. GCP VM 업로드 명령어

`<VM_NAME>`, `<ZONE>`, `<PROJECT_ID>`는 본인의 GCP VM 정보로 치환하세요.

```powershell
# 1) 게시 파일 및 배포 설정을 VM의 /tmp 경로로 업로드
gcloud compute scp --recurse .\publish\labpet-server-linux-x64 YOUR_VM_NAME:/tmp/labpet-publish --zone YOUR_ZONE --project YOUR_PROJECT_ID
gcloud compute scp --recurse .\deploy\gcp YOUR_VM_NAME:/tmp/labpet-gcp --zone YOUR_ZONE --project YOUR_PROJECT_ID

# 2) SSH 접속
gcloud compute ssh YOUR_VM_NAME --zone YOUR_ZONE --project YOUR_PROJECT_ID
```

일반 SSH를 사용할 경우:
```bash
scp -r ./publish/labpet-server-linux-x64 USER@VM_IP:/tmp/labpet-publish
scp -r ./deploy/gcp USER@VM_IP:/tmp/labpet-gcp
ssh USER@VM_IP
```

---

## 3. VM 초기 설정 (최초 1회)

VM에 SSH 접속 후 아래 명령어를 순서대로 실행합니다.

### 3-1. 서비스 전용 계정 및 디렉터리 생성
```bash
sudo useradd --system --no-create-home --shell /usr/sbin/nologin labpet || true
sudo mkdir -p /opt/labpet/releases /var/lib/labpet /etc/labpet
sudo chown -R labpet:labpet /opt/labpet /var/lib/labpet
```

### 3-2. .NET 10 ASP.NET Core Runtime 설치 (Ubuntu 24.04)
```bash
sudo apt-get update
sudo apt-get install -y curl ca-certificates gnupg apt-transport-https
sudo apt-get install -y aspnetcore-runtime-10.0 || {
  # 기본 Ubuntu 저장소에 aspnetcore-runtime-10.0이 아직 없는 경우 Microsoft 공식 패키지 저장소 등록
  curl -fsSL https://packages.microsoft.com/config/ubuntu/24.04/packages-microsoft-prod.deb -o /tmp/packages-microsoft-prod.deb
  sudo dpkg -i /tmp/packages-microsoft-prod.deb
  rm -f /tmp/packages-microsoft-prod.deb
  sudo apt-get update
  sudo apt-get install -y aspnetcore-runtime-10.0
}
dotnet --list-runtimes
```

### 3-3. Caddy 설치 및 HTTPS 리버스 프록시 설정
```bash
sudo apt-get install -y debian-keyring debian-archive-keyring
curl -1sLf 'https://dl.cloudsmith.io/public/caddy/stable/gpg.key' | sudo gpg --dearmor -o /usr/share/keyrings/caddy-stable-archive-keyring.gpg
curl -1sLf 'https://dl.cloudsmith.io/public/caddy/stable/debian.deb.tar.txt' | sudo tee /etc/apt/sources.list.d/caddy-stable.list
sudo apt-get update
sudo apt-get install -y caddy
```

도메인 DNS A 레코드를 VM의 고정 외부 IP로 연결한 뒤, `Caddyfile`의 `YOUR_DOMAIN`을 실제 도메인으로 바꾸어 적용합니다:
```bash
sudo sed 's/YOUR_DOMAIN/실제도메인.example.com/g' /tmp/labpet-gcp/Caddyfile | sudo tee /etc/caddy/Caddyfile >/dev/null
sudo caddy validate --config /etc/caddy/Caddyfile
sudo systemctl enable --now caddy
sudo systemctl reload caddy
```

### 3-4. 서버 비밀키 및 환경변수 파일 생성
```bash
SECRET=$(openssl rand -base64 32)
sudo tee /etc/labpet/labpet.env >/dev/null <<EOF
LABPET_LISTEN_URL=http://127.0.0.1:5000
LABPET_REQUIRE_SECRET=true
LABPET_SERVER_SECRET=${SECRET}
LABPET_DATA_DIR=/var/lib/labpet
EOF
sudo chmod 600 /etc/labpet/labpet.env
sudo chown root:labpet /etc/labpet/labpet.env
sudo chmod 640 /etc/labpet/labpet.env
```

### 3-5. systemd 서비스 등록 및 journald 로그 용량 제한
```bash
sudo cp /tmp/labpet-gcp/labpet.service /etc/systemd/system/labpet.service
sudo systemctl daemon-reload

# e2-micro 디스크 보호를 위한 journald 용량 상한 설정 (100MB)
sudo mkdir -p /etc/systemd/journald.conf.d
sudo tee /etc/systemd/journald.conf.d/labpet.conf >/dev/null <<'EOF'
[Journal]
SystemMaxUse=100M
MaxRetentionSec=14day
EOF
sudo systemctl restart systemd-journald
```

### 3-6. 첫 배포 및 헬스 체크
```bash
sed -i 's/\r$//' /tmp/labpet-gcp/deploy.sh
sudo bash /tmp/labpet-gcp/deploy.sh /tmp/labpet-publish

# 로컬 루프백 확인
curl -sS http://127.0.0.1:5000/health
# HTTPS 외부 확인
curl -sS https://실제도메인.example.com/health
```

---

## 4. 방화벽 설정 (TCP 80/443만 개방)

### 4-1. GCP VPC 방화벽 (GCP 콘솔 또는 `gcloud`)
- 인바운드 허용: `TCP:80` (ACME TLS 인증서 발급·HTTP→HTTPS 리다이렉트), `TCP:443` (HTTPS & WSS)
- TCP `5000`은 외부에서 직접 접근하지 못하도록 개방하지 않습니다(`127.0.0.1:5000` 바인딩).

```bash
gcloud compute firewall-rules create labpet-allow-web \
  --project YOUR_PROJECT_ID \
  --allow tcp:80,tcp:443 \
  --target-tags http-server,https-server
```

### 4-2. VM 내부 UFW 설정 시
```bash
sudo ufw allow OpenSSH
sudo ufw allow 80/tcp
sudo ufw allow 443/tcp
sudo ufw --force enable
sudo ufw status
```

---

## 5. 일상 운영 명령어 (시작·중지·업데이트·로그·롤백)

```bash
# 서비스 상태 및 최근 로그 확인
sudo systemctl status labpet
sudo journalctl -u labpet -n 100 --no-pager

# 실시간 로그 추적
sudo journalctl -u labpet -f

# 시작 / 중지 / 재시작
sudo systemctl start labpet
sudo systemctl stop labpet
sudo systemctl restart labpet

# 업데이트 배포 (새 /tmp/labpet-publish 업로드 후)
sudo bash /tmp/labpet-gcp/deploy.sh /tmp/labpet-publish

# 수동 롤백이 필요할 때 (직전 릴리스 확인 후 심볼릭 링크 복원)
ls -lt /opt/labpet/releases
sudo ln -sfn /opt/labpet/releases/<이전_타임스탬프> /opt/labpet/current
sudo systemctl restart labpet
```

---

## 6. Windows 클라이언트 연결 설정

`ServerConnectionOptions`는 **`LABPET_SERVER_URL` 환경변수 → `%AppData%\LabPet\server.json` → 릴리스 빌드 기본 포함 URL(`LabPetDefaultServerUrl`)** 순으로 서버 주소를 읽습니다.

### 방법 A: GitHub Actions 릴리스 자동 주입 (권장 · 배포용)
GitHub 저장소 `Settings → Secrets and variables → Actions`에 `LABPET_SERVER_URL` (Repository secret 또는 variable, 예: `https://실제도메인.example.com`)을 등록해 두면 `.github/workflows/release.yml` 태그 릴리스(`v*.*.*`) 빌드 시 클라이언트 바이너리에 기본 URL이 함께 포함됩니다.
- 이 방식으로 배포·자동 업데이트를 받은 사용자는 **`server.json`을 따로 만들지 않아도 바로 GCP 서버에 연동**됩니다.
- 특정 사용자가 원격 연동을 끄고 싶을 때는 `%AppData%\LabPet\server.json`에 `{"enabled": false}`만 지정하면 비활성화됩니다.

### 방법 B: 개별 PC 설정 파일 (`server.json`)
릴리스 주입 없이 로컬 개발 빌드나 개별 PC에서 지정·재정의하려면 `%AppData%\LabPet\server.json` 파일을 생성합니다 (`deploy/gcp/client-server-config.example.json` 참고):
```json
{
  "enabled": true,
  "serverUrl": "https://실제도메인.example.com"
}
```

### 방법 C: 환경변수
```powershell
$env:LABPET_SERVER_URL = "https://실제도메인.example.com"
```

설정이 없거나 서버가 잠시 중단되어도 기존 LAN P2P 기능은 영향을 받지 않고 그대로 동작합니다.

---

## 7. 예상 비용 및 네트워크 사용량 주의사항 (`e2-micro` 기준)

- **VM/디스크**: GCP Always Free 대상 리전(`us-west1`, `us-central1`, `us-east1`)의 `e2-micro` + 표준 영구 디스크 30GB 이하 구성은 무료 등급 조건 내에 들어갈 수 있습니다. (단, 아시아 리전 `asia-northeast3` 서울 등은 무료 VM 대상이 아니며, 외부 고정 IP/스냅샷/초과 디스크에 따라 소액 과금이 발생할 수 있으므로 GCP 결제 대시보드에서 예산 알림 설정을 권장합니다.)
- **네트워크 이그레스(송신 트래픽)**:
  - 텍스트 말풍선·게임 이벤트·프레즌스는 패킷당 수백~수천 바이트 수준으로 매우 작습니다.
  - **노래 따라하기**는 원격 구독자가 있을 때만 ~15Hz 이하로 릴레이하므로 미구독 시 트래픽이 발생하지 않습니다. 만약 장시간 원격 노래 따라하기를 다수 사용자가 동시에 켜면 프레임 송신량(약 250B × 15Hz ≈ 3.75KB/s ≈ 13.5MB/시간/구독자)이 누적되므로 무료 이그레스 한도(예: 월 1GB 기준)를 넘지 않도록 주의하세요.

---

## 8. 실제 인터넷 환경 최종 검증 방법 (서로 다른 네트워크 PC 2대)

1. 두 대의 Windows PC(예: 집 Wi-Fi와 모바일 핫스팟)에 동일한 `server.json`을 배치하고 LabPet을 실행합니다.
2. `친구` 창 우상단 상태 배지가 **`LAN P2P + 인터넷(GCP)`** 인지 확인합니다.
3. PC A의 **`주변 펫`** 탭에 온라인으로 표시되는 PC B(`인터넷(GCP)`)를 선택해 **친구 신청**을 누르고 PC B에서 수락합니다(기존과 동일한 친구 추가 절차).
4. 양방향 말풍선 메시지, 빠른 이모지, 쓰다듬기, 가위바위보/타이핑/공동 사다리 게임, 수면(`zzz`), 원격 노래 따라하기가 정상 작동하는지 확인합니다.
5. PC B가 종료(오프라인)된 상태에서 PC A가 메시지를 보내면 오프라인으로 인해 전달되지 않음을 확인하고, VM의 `/var/lib/labpet/registry.json`에 메시지 본문이나 암호문이 전혀 저장되지 않았는지 확인합니다.
6. VM에서 `sudo systemctl restart labpet` 실행 후 수 초 내 두 PC의 연결 상태가 자동 재연결되는지 확인합니다.
7. 같은 Wi-Fi로 PC A·B를 옮겼을 때 LAN P2P 경로를 우선 사용하는지 확인합니다.
