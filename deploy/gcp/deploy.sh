#!/usr/bin/env bash
set -euo pipefail

# LabPet.Server 무중단/롤백 지원 배포 스크립트 (Ubuntu 24.04 + systemd 기준)
# 사용법: sudo bash deploy.sh [빌드_아티팩트_디렉터리]
# 기본 아티팩트 경로: /tmp/labpet-publish

ARTIFACT_DIR="${1:-/tmp/labpet-publish}"
APP_ROOT="/opt/labpet"
RELEASES_DIR="${APP_ROOT}/releases"
CURRENT_LINK="${APP_ROOT}/current"
SERVICE_NAME="labpet"
ENV_FILE="/etc/labpet/labpet.env"
DATA_DIR="/var/lib/labpet"
HEALTH_URL="http://127.0.0.1:5000/health"

if [[ "$(id -u)" -ne 0 ]]; then
  echo "[오류] root 권한(sudo)으로 실행해야 합니다." >&2
  exit 1
fi

if [[ ! -f "${ARTIFACT_DIR}/LabPet.Server.dll" ]]; then
  echo "[오류] ${ARTIFACT_DIR}/LabPet.Server.dll 파일을 찾을 수 없습니다." >&2
  exit 1
fi

if [[ ! -f "${ENV_FILE}" ]]; then
  echo "[오류] ${ENV_FILE} 환경변수 파일이 없습니다. labpet.env.example을 참고해 먼저 생성하세요." >&2
  exit 1
fi

if grep -q "REPLACE_WITH_OPENSSL" "${ENV_FILE}"; then
  echo "[오류] ${ENV_FILE}의 LABPET_SERVER_SECRET을 실제 난수 비밀키로 교체한 뒤 다시 실행하세요." >&2
  exit 1
fi

mkdir -p "${RELEASES_DIR}" "${DATA_DIR}"
chown -R labpet:labpet "${DATA_DIR}" "${APP_ROOT}"

TIMESTAMP="$(date -u +%Y%m%d%H%M%S)"
NEW_RELEASE="${RELEASES_DIR}/${TIMESTAMP}"
PREV_RELEASE=""

if [[ -L "${CURRENT_LINK}" ]]; then
  PREV_RELEASE="$(readlink -f "${CURRENT_LINK}" || true)"
elif [[ -d "${CURRENT_LINK}" ]]; then
  # 심볼릭 링크가 아닌 기존 디렉터리였던 경우 안전하게 백업
  PREV_RELEASE="${RELEASES_DIR}/backup-${TIMESTAMP}"
  mv "${CURRENT_LINK}" "${PREV_RELEASE}"
fi

echo "[1/5] 새 릴리스 스테이징: ${NEW_RELEASE}"
mkdir -p "${NEW_RELEASE}"
cp -a "${ARTIFACT_DIR}/." "${NEW_RELEASE}/"
chown -R labpet:labpet "${NEW_RELEASE}"

rollback() {
  echo "[롤백] 배포 검증 실패! 이전 버전으로 복구합니다..." >&2
  systemctl stop "${SERVICE_NAME}" || true
  if [[ -n "${PREV_RELEASE}" && -d "${PREV_RELEASE}" ]]; then
    ln -sfn "${PREV_RELEASE}" "${CURRENT_LINK}"
    systemctl start "${SERVICE_NAME}" || true
    echo "[롤백] ${PREV_RELEASE} 로 복원 완료. 최근 로그:" >&2
  else
    echo "[롤백] 이전 릴리스가 없어 새 배포 디렉터리를 유지한 채 중지했습니다." >&2
  fi
  journalctl -u "${SERVICE_NAME}" -n 40 --no-pager >&2 || true
  exit 1
}

echo "[2/5] 기존 서비스 중지 및 경로 전환"
if systemctl list-unit-files | grep -q "^${SERVICE_NAME}.service"; then
  systemctl stop "${SERVICE_NAME}" || true
fi

ln -sfn "${NEW_RELEASE}" "${CURRENT_LINK}"

echo "[3/5] systemd 서비스 시작"
systemctl daemon-reload
systemctl enable "${SERVICE_NAME}" >/dev/null 2>&1 || true
systemctl start "${SERVICE_NAME}" || rollback

echo "[4/5] 헬스 체크 대기 (${HEALTH_URL})"
HEALTHY=0
for _ in $(seq 1 15); do
  if curl -fsS --max-time 2 "${HEALTH_URL}" >/dev/null 2>&1; then
    HEALTHY=1
    break
  fi
  sleep 1
done

if [[ "${HEALTHY}" -ne 1 ]]; then
  rollback
fi

echo "[5/5] 헬스 체크 통과! 오래된 릴리스 정리 (최근 5개 유지)"
ls -1dt "${RELEASES_DIR}"/*/ 2>/dev/null | tail -n +6 | xargs -r rm -rf --

echo "[완료] LabPet.Server 배포 성공 (${NEW_RELEASE})"
curl -fsS "${HEALTH_URL}"
echo ""
