# Runs on the server. Read-only: it only reads /proc and a few standard tools,
# printing one snapshot per second until the SSH connection closes.
export LC_ALL=C
if [ ! -r /proc/stat ]; then
  echo "@@ERR This server has no /proc filesystem - VPS Monitor needs a Linux server."
  exit 3
fi
echo "@@INFO"
echo "hostname=$(hostname 2>/dev/null || cat /proc/sys/kernel/hostname)"
echo "kernel=$(uname -sr)"
echo "arch=$(uname -m)"
echo "os=$( . /etc/os-release 2>/dev/null; echo "$PRETTY_NAME" )"
echo "cores=$(grep -c '^processor' /proc/cpuinfo)"
echo "cpu=$(grep -m1 -E 'model name|Model' /proc/cpuinfo | cut -d: -f2- | sed 's/^ *//')"
echo "clk=$(getconf CLK_TCK 2>/dev/null || echo 100)"
echo "page=$(getconf PAGESIZE 2>/dev/null || echo 4096)"
echo "user=$(id -un)"
echo "virt=$(systemd-detect-virt 2>/dev/null)"
echo "@@END"
n=0
while :; do
  echo "@@FRAME"
  echo "#uptime"; cat /proc/uptime
  echo "#loadavg"; cat /proc/loadavg
  echo "#stat"; grep -E '^(cpu|procs_|ctxt)' /proc/stat
  echo "#meminfo"; grep -E '^(MemTotal|MemFree|MemAvailable|Buffers|Cached|SReclaimable|Shmem|SwapTotal|SwapFree|Dirty):' /proc/meminfo
  echo "#netdev"; tail -n +3 /proc/net/dev
  echo "#diskstats"; cat /proc/diskstats
  echo "#sockstat"; cat /proc/net/sockstat
  echo "#procs"; cat /proc/[0-9]*/stat 2>/dev/null
  if [ $((n % 5)) -eq 0 ]; then
    echo "#df"; df -kPT 2>/dev/null | tail -n +2
    echo "#ps"; ps -eo pid=,user=,args= 2>/dev/null | cut -c1-240
    echo "#users"; who 2>/dev/null | wc -l
  fi
  if [ $((n % 15)) -eq 0 ]; then
    echo "#listen"; ss -Htuln 2>/dev/null || netstat -tuln 2>/dev/null | tail -n +3
    echo "#failed"; systemctl list-units --state=failed --no-legend --plain 2>/dev/null | awk '{print $1}'
  fi
  echo "@@EOF"
  n=$((n + 1))
  sleep 1
done
