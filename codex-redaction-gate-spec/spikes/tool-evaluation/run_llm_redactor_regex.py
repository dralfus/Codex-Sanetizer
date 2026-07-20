import json
import sys
from pathlib import Path


BASE = Path(__file__).resolve().parent
SRC = BASE / "llm-redactor-src" / "src"
sys.path.insert(0, str(SRC))

from llm_redactor.detect.regex import detect_regex  # noqa: E402


def main() -> None:
    corpus = json.loads((BASE / "corpus.json").read_text(encoding="utf-8"))
    output = []
    for item in corpus:
        spans = detect_regex(item["text"])
        output.append(
            {
                "id": item["id"],
                "findings": [
                    {
                        "kind": span.kind,
                        "category": span.category,
                        "start": span.start,
                        "end": span.end,
                        "source": span.source,
                    }
                    for span in sorted(spans, key=lambda s: (s.start, s.end, s.kind))
                ],
            }
        )
    print(json.dumps(output, indent=2))


if __name__ == "__main__":
    main()
