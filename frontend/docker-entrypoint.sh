#!/bin/sh
# 运行时把 Keycloak 配置注入 index.html 的 window.__ISE_AUTH__——
# 一个镜像走遍所有环境的关键(等价 goodcrew 后端 AuthConfigInjection,
# ISEStudio 后端不托管前端静态文件,注入点移到 nginx 容器)。
# 两个 env 都配了才注入;没配则不动 index.html,前端回落构建期
# VITE_AUTH_*(dev)或完全禁用(现有 cookie 登录路径)。
set -e

if [ -n "$ISE_AUTH_AUTHORITY" ] && [ -n "$ISE_AUTH_CLIENT_ID" ]; then
  # sed 替换文本里的 & 和 | 会破坏语法,先转义(URL 一般没有,防一手)。
  authority=$(printf '%s' "$ISE_AUTH_AUTHORITY" | sed 's/[&|\\]/\\&/g')
  clientId=$(printf '%s' "$ISE_AUTH_CLIENT_ID" | sed 's/[&|\\]/\\&/g')
  inject="<script>window.__ISE_AUTH__ = {\"authority\":\"${authority}\",\"clientId\":\"${clientId}\"};</script></head>"
  sed -i "s|</head>|${inject}|" /usr/share/nginx/html/index.html
fi

exec "$@"
