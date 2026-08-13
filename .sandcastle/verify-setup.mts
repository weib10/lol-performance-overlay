import { execFileSync } from "node:child_process";
import { readFileSync } from "node:fs";

import { IMAGE_NAME } from "./runtime.mts";

function command(program: string, args: string[]): string {
  return execFileSync(program, args, { encoding: "utf8" }).trim();
}

const manifest = JSON.parse(readFileSync("package.json", "utf8")) as {
  devDependencies: Record<string, string>;
};
const lock = JSON.parse(readFileSync("package-lock.json", "utf8")) as {
  packages: Record<string, { version?: string; integrity?: string }>;
};

if (process.version !== "v22.23.2") {
  throw new Error(`Expected Node v22.23.2; received ${process.version}.`);
}
if (command("npm", ["--version"]) !== "10.9.8") {
  throw new Error("Expected npm 10.9.8.");
}
if (manifest.devDependencies["@ai-hero/sandcastle"] !== "0.12.0") {
  throw new Error("Sandcastle is not pinned to 0.12.0.");
}
if (manifest.devDependencies.tsx !== "4.23.12") {
  throw new Error("tsx is not pinned to 4.23.12.");
}

const sandcastleLock = lock.packages["node_modules/@ai-hero/sandcastle"];
if (
  sandcastleLock?.version !== "0.12.0" ||
  sandcastleLock.integrity !==
    "sha512-kdQ414rM8t1QiWeqZ3Klz4KSd0PqQG4bRVuqGpRDUomWhojSZkEAc1tbcEcThVmBEaHkCt8LmYR49vqEPNIoYQ=="
) {
  throw new Error("package-lock.json does not contain the verified Sandcastle artifact.");
}

command("npm", ["ls", "--depth=0"]);
const imageVersions = command("docker", [
  "run",
  "--rm",
  "--entrypoint",
  "sh",
  IMAGE_NAME,
  "-lc",
  "node --version && npm --version && dotnet --version && codex --version && if command -v gh >/dev/null; then exit 9; fi",
]).split("\n");

const expected = ["v22.23.2", "10.9.8", "8.0.423", "codex-cli 0.147.0"];
if (JSON.stringify(imageVersions) !== JSON.stringify(expected)) {
  throw new Error(`Unexpected image versions: ${imageVersions.join(", ")}`);
}

console.log("Sandcastle setup verification: PASS");
console.log(`Image: ${IMAGE_NAME}`);
console.log(`Node/npm/.NET/Codex: ${expected.join(" / ")}`);
console.log("Local packages: @ai-hero/sandcastle 0.12.0 / tsx 4.23.12");
console.log("GitHub CLI in image: absent");
