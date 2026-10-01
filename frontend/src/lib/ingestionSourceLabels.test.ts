import { describe, expect, it } from "vitest"

import { sourceConfigFieldLabel, sourceKindLabel } from "./ingestionSourceLabels"

describe("ingestion source labels", () => {
  it("uses localized labels for source kinds and config fields", () => {
    const translate = (key: string) => ({
      "ingestionSources.kind.github_issues": "GitHub issues",
      "ingestionSources.configField.service_account_key": "Service account JSON key",
    })[key] ?? key

    expect(sourceKindLabel("github_issues", translate)).toBe("GitHub issues")
    expect(sourceConfigFieldLabel("service_account_key", translate)).toBe("Service account JSON key")
  })

  it("humanizes unknown kinds and fields instead of showing raw keys", () => {
    const translate = (key: string) => key

    expect(sourceKindLabel("future_connector", translate)).toBe("Future connector")
    expect(sourceConfigFieldLabel("custom_option", translate)).toBe("Custom option")
  })
})