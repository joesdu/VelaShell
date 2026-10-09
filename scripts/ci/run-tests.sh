#!/usr/bin/env bash
# CI 的测试步骤(.github/workflows/ci.yml 在仓库根目录调用;Linux 上整个脚本跑在 xvfb-run 里,
# 下面起的每个进程都继承同一块虚拟屏)。
#
# VelaShell.Plugin.Ai.Tests 里三组 headless UI 用例 —— 聊天面板、接入向导、设置页 —— 占了这个工程九成以上的时间:
# 每条都要真等一段时间让面板的异步链落定,而一个进程里只有一个 Avalonia UI 线程,只能一条接一条地跑。
# #582 把这个工程从 587 条加到 1400 多条之后,单它一个就是整个测试步骤的最长一段
# (macOS 上 22 分钟,别的工程都在 2 分钟内跑完),整个 CI 从 7 分钟拖到 25 分钟以上。
#
# 所以这三组各起一个进程,与解决方案的其余测试同时跑。每个进程仍只有一个 UI 线程、组内照旧串行;
# 插件没有跨实例的静态状态,TestPluginContext 的数据目录每个实例各一个,拆开跑不改变任何一条用例看到的东西。
# 用例本身怎么等,见 tests/VelaShell.Plugin.Ai.Tests/HeadlessPump.cs。
set -uo pipefail

base='TestCategory!=DockerIntegration&TestCategory!=CrossPlatform&TestCategory!=Interop'
project='VelaShell.Plugin.Ai.Tests'
groups=(ChatPanelViewUiTests ProviderSetupViewUiTests SettingsViewUiTests)
common=(-c Debug --no-build --nologo --results-directory artifacts/trx)

mkdir -p artifacts/logs
rest="$base"
pids=()
for group in "${groups[@]}"; do
  rest+="&FullyQualifiedName!~$project.$group"
  dotnet test "tests/$project" "${common[@]}" --filter "$base&FullyQualifiedName~$project.$group" \
    --logger "trx;LogFilePrefix=ci-$group" > "artifacts/logs/$group.log" 2>&1 &
  pids+=($!)
done

status=0
dotnet test VelaShell.slnx "${common[@]}" --filter "$rest" --logger "trx;LogFilePrefix=ci" || status=$?

for i in "${!groups[@]}"; do
  code=0
  wait "${pids[$i]}" || code=$?
  echo "::group::$project / ${groups[$i]}(退出码 $code)"
  cat "artifacts/logs/${groups[$i]}.log"
  echo "::endgroup::"
  if [ "$code" -ne 0 ]; then
    echo "::error::$project / ${groups[$i]} 没有全过(退出码 $code),详情见上面折起来的那一组。"
    status=$code
  fi
done
exit "$status"
