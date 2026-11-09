import { describe, expect, it } from "vitest";
import { isDesignPageEnabled, isMockGatewayEnabled } from "./feature-flags";

describe("isDesignPageEnabled", () => {
  it("is on in development", () => {
    expect(isDesignPageEnabled({ NODE_ENV: "development" })).toBe(true);
  });

  it("is off in production", () => {
    expect(isDesignPageEnabled({ NODE_ENV: "production" })).toBe(false);
  });

  it("can be forced on in a production build for Lighthouse/axe in CI", () => {
    expect(isDesignPageEnabled({ NODE_ENV: "production", ENABLE_DESIGN_PAGE: "1" })).toBe(true);
  });
});

describe("isMockGatewayEnabled", () => {
  it("is on in development and off in production", () => {
    expect(isMockGatewayEnabled({ NODE_ENV: "development" })).toBe(true);
    expect(isMockGatewayEnabled({ NODE_ENV: "production" })).toBe(false);
  });

  it("can be switched on for a demo deployment", () => {
    expect(isMockGatewayEnabled({ NODE_ENV: "production", ENABLE_MOCK_GATEWAY: "1" })).toBe(true);
    expect(isMockGatewayEnabled({ NODE_ENV: "production", ENABLE_MOCK_GATEWAY: "0" })).toBe(false);
  });
});
