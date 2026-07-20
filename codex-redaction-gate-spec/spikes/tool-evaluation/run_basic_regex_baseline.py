import json
import re
from pathlib import Path


BASE = Path(__file__).resolve().parent
CORPUS = BASE / "corpus.json"

PATTERNS = {
    "URL": re.compile(r"https?://[^\s)]+", re.I),
    "PRIVATE_IPV4": re.compile(r"\b(?:10\.\d{1,3}|172\.(?:1[6-9]|2\d|3[0-1])|192\.168)\.\d{1,3}\.\d{1,3}\b"),
    "CIDR": re.compile(r"\b(?:10|172|192)\.\d{1,3}\.\d{1,3}\.\d{1,3}/\d{1,2}\b"),
    "EMAIL": re.compile(r"\b[A-Z0-9._%+-]+@[A-Z0-9.-]+\.[A-Z]{2,}\b", re.I),
    "WINDOWS_PATH": re.compile(r"\b[A-Za-z]:\\(?:[^\\/:*?\"<>|\r\n]+\\)*[^\\/:*?\"<>|\r\n]*"),
    "JWT": re.compile(r"\beyJ[A-Za-z0-9_-]+\.[A-Za-z0-9_-]+\.[A-Za-z0-9_-]+\b"),
    "PRIVATE_KEY": re.compile(r"-----BEGIN [A-Z ]*PRIVATE KEY-----[\s\S]*?-----END [A-Z ]*PRIVATE KEY-----"),
    "CONNECTION_STRING": re.compile(r"\b(?:postgres|postgresql|mysql|mongodb)://[^\s]+", re.I),
    "BEARER": re.compile(r"\bBearer\s+[A-Za-z0-9._~+/=-]{12,}\b", re.I),
}


def main() -> None:
    corpus = json.loads(CORPUS.read_text(encoding="utf-8"))
    output = []
    for item in corpus:
        findings = []
        for entity_type, pattern in PATTERNS.items():
            for match in pattern.finditer(item["text"]):
                findings.append(
                    {
                        "entity_type": entity_type,
                        "start": match.start(),
                        "end": match.end(),
                    }
                )
        output.append({"id": item["id"], "findings": sorted(findings, key=lambda x: (x["start"], x["end"], x["entity_type"]))})
    print(json.dumps(output, indent=2))


if __name__ == "__main__":
    main()
