import type { MessageKey, Translate } from "@/lib/i18n"

const sourceKindKeys: Record<string, MessageKey> = {
  folder: "ingestionSources.kind.folder",
  url: "ingestionSources.kind.url",
  rss: "ingestionSources.kind.rss",
  custom: "ingestionSources.kind.custom",
  github_issues: "ingestionSources.kind.github_issues",
  jira_issues: "ingestionSources.kind.jira_issues",
  s3: "ingestionSources.kind.s3",
  gcs: "ingestionSources.kind.gcs",
  webdav: "ingestionSources.kind.webdav",
  notion: "ingestionSources.kind.notion",
  api: "ingestionSources.kind.api",
  statements: "ingestionSources.kind.statements",
}

const configFieldKeys: Record<string, MessageKey> = {
  repo: "ingestionSources.configField.repo",
  auth_header: "ingestionSources.configField.auth_header",
  max_pages: "ingestionSources.configField.max_pages",
  base_url: "ingestionSources.configField.base_url",
  project: "ingestionSources.configField.project",
  deployment: "ingestionSources.configField.deployment",
  urls: "ingestionSources.configField.urls",
  feed_url: "ingestionSources.configField.feed_url",
  endpoint: "ingestionSources.configField.endpoint",
  bucket: "ingestionSources.configField.bucket",
  prefix: "ingestionSources.configField.prefix",
  region: "ingestionSources.configField.region",
  access_key_id: "ingestionSources.configField.access_key_id",
  secret_access_key: "ingestionSources.configField.secret_access_key",
  session_token: "ingestionSources.configField.session_token",
  service_account_key: "ingestionSources.configField.service_account_key",
  path: "ingestionSources.configField.path",
  username: "ingestionSources.configField.username",
  password: "ingestionSources.configField.password",
  max_depth: "ingestionSources.configField.max_depth",
  token: "ingestionSources.configField.token",
  scope: "ingestionSources.configField.scope",
  query: "ingestionSources.configField.query",
  page_id: "ingestionSources.configField.page_id",
  database_id: "ingestionSources.configField.database_id",
}

function humanize(value: string): string {
  const words = value.replace(/[_-]+/g, " ").trim()
  return words ? words[0].toUpperCase() + words.slice(1) : value
}

export function sourceKindLabel(kind: string, t: Translate): string {
  const key = sourceKindKeys[kind]
  return key ? t(key) : humanize(kind)
}

export function sourceConfigFieldLabel(field: string, t: Translate): string {
  const key = configFieldKeys[field]
  return key ? t(key) : humanize(field)
}