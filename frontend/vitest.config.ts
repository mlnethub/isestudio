import { defineConfig } from "vitest/config"

// unit test 只收 src 下的 *.test.ts;e2e/*.spec.ts 是 Playwright 的领地,
// 被 vitest 收集会报 "Playwright Test did not expect test.describe()"。
export default defineConfig({
  test: {
    include: ["src/**/*.test.ts"],
  },
})
