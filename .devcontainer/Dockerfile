# DaerD の DevContainer。
#
# ベースは game-ci の Unity Editor イメージ。Unity 本体 (/opt/unity/Editor/Unity)、
# 必要な共有ライブラリ、xvfb、`unity-editor` ラッパー、そして固定 machine-id が
# すでに入っている。machine-id が固定されているおかげで、Personal ライセンス
# (.ulf) はコンテナを再ビルドしても有効なまま使い回せる。
#
# その上に、元の node:20 ベースの環境（node 20 / Claude Code / zsh+p10k /
# git-delta / gh など）を載せ直している。

ARG UNITY_VERSION=2022.3.22f1
ARG UNITY_IMAGE_VERSION=3
FROM unityci/editor:ubuntu-${UNITY_VERSION}-base-${UNITY_IMAGE_VERSION}

# FROM をまたぐと ARG は消えるので、使う分だけ宣言し直して ENV に焼く。
ARG UNITY_VERSION
ENV UNITY_VERSION="${UNITY_VERSION}"

ARG TZ
ENV TZ="$TZ"
ENV DEBIAN_FRONTEND=noninteractive

# ベース (ubuntu:22.04) に足りない開発ツール。Unity 側の依存はベースイメージが
# 済ませているので、ここは純粋に「元の devcontainer にあったもの」だけ。
RUN apt-get -q update \
  && apt-get -q install -y --no-install-recommends \
  less \
  procps \
  sudo \
  fzf \
  zsh \
  man-db \
  gnupg2 \
  iptables \
  ipset \
  iproute2 \
  dnsutils \
  aggregate \
  nano \
  vim \
  tzdata \
  locales \
  # VS Code / Claude Code が LANG=en_US.UTF-8 を送ってくるのに、ベースイメージには
  # C.UTF-8 しか無くて毎コマンド setlocale 警告が出る。生成しておく。
  && sed -i 's/^# *\(en_US.UTF-8\)/\1/' /etc/locale.gen && locale-gen \
  && if [ -n "$TZ" ] && [ -e "/usr/share/zoneinfo/$TZ" ]; then \
       ln -snf "/usr/share/zoneinfo/$TZ" /etc/localtime && echo "$TZ" > /etc/timezone; \
     fi \
  && apt-get clean && rm -rf /var/lib/apt/lists/*

# Node.js 20 (NodeSource)。ベースが Ubuntu なので node イメージの代わりに自前で入れる。
RUN mkdir -p /etc/apt/keyrings \
  && curl -fsSL https://deb.nodesource.com/gpgkey/nodesource-repo.gpg.key \
  | gpg --dearmor -o /etc/apt/keyrings/nodesource.gpg \
  && echo "deb [signed-by=/etc/apt/keyrings/nodesource.gpg] https://deb.nodesource.com/node_20.x nodistro main" \
  > /etc/apt/sources.list.d/nodesource.list \
  && apt-get -q update \
  && apt-get -q install -y --no-install-recommends nodejs \
  && apt-get clean && rm -rf /var/lib/apt/lists/*

# GitHub CLI。Ubuntu の標準リポジトリには無いので公式 apt リポジトリから。
RUN curl -fsSL https://cli.github.com/packages/githubcli-archive-keyring.gpg \
  -o /etc/apt/keyrings/githubcli-archive-keyring.gpg \
  && chmod go+r /etc/apt/keyrings/githubcli-archive-keyring.gpg \
  && echo "deb [arch=$(dpkg --print-architecture) signed-by=/etc/apt/keyrings/githubcli-archive-keyring.gpg] https://cli.github.com/packages stable main" \
  > /etc/apt/sources.list.d/github-cli.list \
  && apt-get -q update \
  && apt-get -q install -y --no-install-recommends gh \
  && apt-get clean && rm -rf /var/lib/apt/lists/*

ARG GIT_DELTA_VERSION=0.18.2
RUN ARCH=$(dpkg --print-architecture) \
  && wget -q "https://github.com/dandavison/delta/releases/download/${GIT_DELTA_VERSION}/git-delta_${GIT_DELTA_VERSION}_${ARCH}.deb" \
  && dpkg -i "git-delta_${GIT_DELTA_VERSION}_${ARCH}.deb" \
  && rm "git-delta_${GIT_DELTA_VERSION}_${ARCH}.deb"

ARG USERNAME=node

# ubuntu:22.04 には uid 1000 のユーザーがいないので作る。uid/gid を 1000 に固定
# するのは、既存の名前付きボリューム (/home/node/.claude など) の所有者と
# 揃えるため。
RUN groupadd --gid 1000 "$USERNAME" \
  && useradd --uid 1000 --gid 1000 --shell /bin/zsh --create-home "$USERNAME"

# bash 履歴の永続化
RUN mkdir /commandhistory \
  && touch /commandhistory/.bash_history \
  && chown -R "$USERNAME:$USERNAME" /commandhistory

ENV DEVCONTAINER=true

# ボリュームのマウント先を先に作って node 所有にしておく。空のボリュームは
# マウント先ディレクトリの所有者を引き継ぐので、これをやらないと root 所有の
# まま生成されて node から書けなくなる。
RUN mkdir -p \
  /workspace \
  /home/node/.claude \
  /home/node/unity-testproject \
  /home/node/.unity-license \
  /home/node/.config/unity3d \
  /home/node/.local/share/unity3d \
  && chown -R "$USERNAME:$USERNAME" /workspace /home/node

# Unity はライセンスファイルをここに置く。node で動かすので書けるようにしておく。
RUN mkdir -p /usr/share/unity3d/config \
  && chown -R "$USERNAME:$USERNAME" /usr/share/unity3d

WORKDIR /workspace

USER $USERNAME

ENV NPM_CONFIG_PREFIX=/home/node/.npm-global
ENV PATH=$PATH:/home/node/.npm-global/bin
ENV SHELL=/bin/zsh
ENV EDITOR=nano
ENV VISUAL=nano

ARG ZSH_IN_DOCKER_VERSION=1.2.0
RUN sh -c "$(wget -O- https://github.com/deluan/zsh-in-docker/releases/download/v${ZSH_IN_DOCKER_VERSION}/zsh-in-docker.sh)" -- \
  -p git \
  -p fzf \
  -a "source /usr/share/doc/fzf/examples/key-bindings.zsh" \
  -a "source /usr/share/doc/fzf/examples/completion.zsh" \
  -a "export PROMPT_COMMAND='history -a' && export HISTFILE=/commandhistory/.bash_history" \
  -x

ARG CLAUDE_CODE_VERSION=latest
RUN npm install -g @anthropic-ai/claude-code@${CLAUDE_CODE_VERSION}

# vrc-get: VPM パッケージ (VRChat SDK など) をコンテナ内で入れるための CLI。
# 静的リンクの単一バイナリなので置くだけでよい。
ARG VRC_GET_VERSION=1.9.2
RUN mkdir -p /home/node/.local/bin \
  && wget -qO /home/node/.local/bin/vrc-get \
  "https://github.com/vrc-get/vrc-get/releases/download/v${VRC_GET_VERSION}/x86_64-unknown-linux-musl-vrc-get" \
  && chmod +x /home/node/.local/bin/vrc-get
ENV PATH=$PATH:/home/node/.local/bin

# テスト用スクリプトをどこからでも呼べるように PATH に通す。
ENV PATH=$PATH:/workspace/.devcontainer/unity
