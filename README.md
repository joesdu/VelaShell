# gh-pages —— VelaShell 官网

这个分支是 <https://joesdu.github.io/VelaShell/> 的**发布目录**,不是源码分支:
`index.html` 就是站点本身,没有构建步骤,推上来即生效。

```
index.html                         站点(内联 CSS/JS,单文件)
assets/velashell.png               应用图标,兼作 favicon 与 logo
assets/social-preview.png          og:image,取自 build/social-preview/
assets/shots/*.webp                实机截图(Hero 与「截图」分区)
assets/mascot/chibi-*.webp         看板娘 Q 版贴纸:透明底 + 白描边
assets/mascot/key-visual.webp      看板娘主视觉
assets/mascot/character-sheet.webp 设定图
assets/mascot/faces/*.webp         六种表情,从设定图裁出
assets/mascot/cat-emblem.svg       黑猫玩偶徽标(页脚)
.nojekyll                          关掉 Jekyll,按静态文件原样发布
```

## 改站点

直接改 `index.html` 再推本分支即可 —— main 受 ruleset 保护要走 PR,本分支不受限,
改文案不必开 PR。

视觉与宣传视频封面同一套:深青 → 藏青的双色辉光底、`VelaShell` 字标的 “Shell” 走青绿到淡紫的渐变、
看板娘以白描边贴纸的形式出现。品牌青绿 `#2FD8AB` / `#19C39A` 与
[`build/social-preview/social-preview.html`](https://github.com/joesdu/VelaShell/blob/main/build/social-preview/social-preview.html)
一致;藏青紫辉光与点阵底是官网新加的,社交预览图还没跟上。

## 素材从哪来

**看板娘只用原画,不重画。** 全部取自 main 分支的 [`mascot/`](https://github.com/joesdu/VelaShell/tree/main/mascot):

- `chibi-*.webp`:`mascot/chibi-*.png` 是白底图,从四边泼洒近白色当背景抠掉,边缘两圈按「白 → 透明」反解去白边,
  再模糊 alpha 提阈值外扩一圈白描边(与宣传视频合成器里贴纸的做法相同),缩到 520 宽存 WebP。
- `key-visual.webp` / `character-sheet.webp`:原图直接转 WebP。
- `faces/*.webp`:设定图底部「表情展示」六格,各裁 126×126(开心 / 兴奋 / 默认 / 好奇 / 生气 / 害羞)。

`assets/shots/` 是宣传视频的原始录屏(实机界面)抽帧,1600 宽 WebP。画面里有侧栏的会话名与内网地址,
与宣传视频画面一致;换截图时注意别带进公网 IP 与个人文件。

## 下载区不写死链接

Release 资产名带版本号(`VelaShell-1.4.2-win-x64.zip`),写死的话每发一版就断。
站点运行时读 `api.github.com/repos/joesdu/VelaShell/releases/latest`,填版本号(顶部小标签、规格表)与各平台直链;
接口取不到(离线、限流)就退回 `releases/latest` 页面,按钮始终可用 —— 所以发新版**不用动这个分支**。
