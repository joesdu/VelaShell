#!/usr/bin/env bash
# CI 的测试步骤(.github/workflows/ci.yml 在仓库根目录调用;Linux 上整个脚本跑在 xvfb-run 里,
# 下面起的每个进程都继承同一块虚拟屏)。
#
# VelaShell.Plugin.Ai.Tests 里三组 headless UI 用例 —— 聊天面板、接入向导、设置页 —— 占了这个工程九成以上的时间:
# 每条都要真等一段时间让面板的异步链落定,而一个进程里只有一个 Avalonia UI 线程,只能一条接一条地跑。
# #582 把这个工程从 587 条加到 1400 多条之后,单它一个就是整个测试步骤的最长一段
# (macOS 上 22 分钟,别的工程都在 2 分钟内跑完),整个 CI 从 7 分钟拖到 25 分钟以上。
#
# 所以这三组各起一个进程并行跑。每个进程仍只有一个 UI 线程、组内照旧串行;
# 插件没有跨实例的静态状态,TestPluginContext 的数据目录每个实例各一个,拆开跑不改变任何一条用例看到的东西。
# Windows 先跑完解决方案的其余测试,再启动这三组:run 41561814649 中两组的首条用例同时
# 撞上 60 秒超时,第三组首条也用了 56 秒,后续用例恢复正常。避免 UI 冷启动与其余测试抢资源,
# 不放宽单测超时。Linux / macOS 仍让三组与解决方案同时跑。
# 用例本身怎么等,见 tests/VelaShell.Plugin.Ai.Tests/HeadlessPump.cs。
#
# VelaShell.Tests(宿主,三千多条、整套串行)是其余工程里最长的一个,也单独起进程、一开始就跑:
# 跟着解决方案跑时,dotnet test 按 slnx 里的字母序一次只开 CPU 核数那么多个工程(GitHub 的 runner 是 4 个),
# 它排在倒数第二,要等前面的工程腾出位置 —— run 44429052817 的 Windows 作业里它晚开跑 107 秒,
# 而它恰好就是这一段的最长一个,晚开多少整段就拖长多少。解决方案那一趟按命名空间把它滤掉
# (它的用例都在 VelaShell.Tests.* 下,其余工程没有用例落在这个命名空间里),只剩一次零用例的装载。
# Windows 上三组 UI 用例仍等其余测试 —— 连同它 —— 全部跑完才起,与原来的次序约束一致。
set -uo pipefail

base='TestCategory!=DockerIntegration&TestCategory!=CrossPlatform&TestCategory!=Interop'
project='VelaShell.Plugin.Ai.Tests'
groups=(ChatPanelViewUiTests ProviderSetupViewUiTests SettingsViewUiTests)
host='VelaShell.Tests'
common=(-c Debug --no-build --nologo --results-directory artifacts/trx)

mkdir -p artifacts/logs
rest="$base&FullyQualifiedName!~$host."
for group in "${groups[@]}"; do
  rest+="&FullyQualifiedName!~$project.$group"
done

status=0
run_rest() {
  dotnet test VelaShell.slnx "${common[@]}" --filter "$rest" --logger "trx;LogFilePrefix=ci" || status=$?
}

# 后台跑的那几个进程:名字(日志文件名)与 pid 一一对应,最后统一等、统一打日志
names=()
pids=()
launch() { # launch <名字> <dotnet test 的其余参数…>
  local name=$1
  shift
  dotnet test "$@" --logger "trx;LogFilePrefix=ci-$name" > "artifacts/logs/$name.log" 2>&1 &
  names+=("$name")
  pids+=($!)
}

# 等一个后台进程收尾,把它的日志折叠打出来;失败记下退出码
finish() { # finish <下标>
  local i=$1 code=0
  wait "${pids[$i]}" || code=$?
  echo "::group::${names[$i]}(退出码 $code)"
  cat "artifacts/logs/${names[$i]}.log"
  echo "::endgroup::"
  if [ "$code" -ne 0 ]; then
    echo "::error::${names[$i]} 没有全过(退出码 $code),详情见上面折起来的那一组。"
    status=$code
  fi
}

start_ui_groups() {
  for group in "${groups[@]}"; do
    launch "$group" "tests/$project" "${common[@]}" --filter "$base&FullyQualifiedName~$project.$group"
  done
}

windows=false
case "${RUNNER_OS:-$OSTYPE}" in
  Windows|msys*|cygwin*) windows=true ;;
esac

launch "$host" "tests/$host" "${common[@]}" --filter "$base"
if "$windows"; then
  run_rest
  finish 0 # 宿主测试也算「其余测试」:等它跑完再起 UI 组
  start_ui_groups
  for i in "${!pids[@]}"; do
    [ "$i" -eq 0 ] || finish "$i"
  done
else
  start_ui_groups
  run_rest
  for i in "${!pids[@]}"; do
    finish "$i"
  done
fi
exit "$status"
