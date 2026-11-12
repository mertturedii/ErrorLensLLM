#!/usr/bin/env bash
# Runs the collector, the three demo modules and the traffic generator as local processes.
#
#   scripts/demo.sh start                          collector + dashboard + kayit/not/odeme 1.0.0 + traffic
#   scripts/demo.sh collector                      collector (ingestion + incident analysis) + dashboard
#   Dashboard: http://localhost:5200
#   scripts/demo.sh deploy <module> <version> [fault...]   restart one module as a new release
#   scripts/demo.sh status | stop
#
# Example incident: scripts/demo.sh deploy kayit 1.1.0 kayit-missing-column
# Needs PostgreSQL: docker compose -f deploy/docker-compose.yml up -d
set -euo pipefail

root="$(cd "$(dirname "$0")/.." && pwd)"
state="$root/.demo"
mkdir -p "$state/logs" "$state/pids"

# macOS ships bash 3.2, which has no associative arrays
port() {
  case $1 in collector) echo 5025 ;; dashboard) echo 5200 ;; kayit) echo 5101 ;; not) echo 5102 ;; odeme) echo 5103 ;; traffic) echo 5100 ;; esac
}

build() {
  dotnet build "$root/src/ErrorLensLLM.Collector" -c Release -v q -nologo >/dev/null
  dotnet build "$root/src/ErrorLensLLM.Dashboard" -c Release -v q -nologo >/dev/null
  dotnet build "$root/samples/ErrorLensLLM.Demo" -c Release -v q -nologo >/dev/null
}

wait_healthy() {
  local name=$1
  for _ in $(seq 1 60); do
    curl -sf "http://localhost:$(port "$name")/health" >/dev/null && return 0
    sleep 0.5
  done
  echo "$name did not become healthy, see $state/logs/$name.log" >&2
  return 1
}

run() {
  local name=$1; shift
  stop_one "$name"
  if [[ $name == collector ]]; then
    dotnet "$root/src/ErrorLensLLM.Collector/bin/Release/net8.0/ErrorLensLLM.Collector.dll" \
      --urls "http://localhost:$(port collector)" --contentRoot "$root/src/ErrorLensLLM.Collector" \
      >>"$state/logs/collector.log" 2>&1 &
  elif [[ $name == dashboard ]]; then
    dotnet "$root/src/ErrorLensLLM.Dashboard/bin/Release/net8.0/ErrorLensLLM.Dashboard.dll" \
      --urls "http://localhost:$(port dashboard)" --contentRoot "$root/src/ErrorLensLLM.Dashboard" \
      >>"$state/logs/dashboard.log" 2>&1 &
  else
    dotnet "$root/samples/ErrorLensLLM.Demo/bin/Release/net8.0/ErrorLensLLM.Demo.dll" \
      --urls "http://localhost:$(port "$name")" --contentRoot "$root/samples/ErrorLensLLM.Demo" \
      --Demo:Module="$name" "$@" >>"$state/logs/$name.log" 2>&1 &
  fi
  echo $! >"$state/pids/$name"
  wait_healthy "$name"
  echo "started $name $*"
}

stop_one() {
  local file="$state/pids/$1"
  [[ -f $file ]] || return 0
  local pid; pid=$(cat "$file")
  if kill -0 "$pid" 2>/dev/null; then
    kill "$pid"
    for _ in $(seq 1 20); do kill -0 "$pid" 2>/dev/null || break; sleep 0.25; done
  fi
  rm -f "$file"
}

case "${1:-}" in
  start)
    build
    run collector
    run dashboard
    run kayit --Demo:Version=1.0.0
    run not --Demo:Version=1.0.0
    run odeme --Demo:Version=1.0.0
    run traffic
    ;;
  collector)
    build
    run collector
    run dashboard
    ;;
  deploy)
    module=${2:?module}; version=${3:?version}; shift 3
    args=(--Demo:Version="$version")
    i=0; for fault in "$@"; do args+=("--Demo:Faults:$i=$fault"); i=$((i + 1)); done
    run "$module" "${args[@]}"
    ;;
  status)
    for name in collector dashboard kayit not odeme traffic; do
      printf "%-10s " "$name"; curl -sf "http://localhost:$(port "$name")/health" || printf "down"; echo
    done
    ;;
  stop)
    for name in traffic odeme not kayit dashboard collector; do stop_one "$name"; done
    echo stopped
    ;;
  *)
    sed -n '2,10p' "$0"; exit 1 ;;
esac
