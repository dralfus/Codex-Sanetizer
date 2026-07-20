import json
import sys
from pathlib import Path

from presidio_analyzer import AnalyzerEngine, Pattern, PatternRecognizer, RecognizerRegistry
from presidio_anonymizer import AnonymizerEngine
from presidio_anonymizer.entities import OperatorConfig


BASE = Path(__file__).resolve().parent
CORPUS = BASE / "corpus.json"
TERMS = BASE / "custom_policy_terms.csv"


def build_registry() -> RecognizerRegistry:
    registry = RecognizerRegistry()
    registry.load_predefined_recognizers()

    import csv

    with TERMS.open("r", encoding="utf-8", newline="") as f:
        for row in csv.DictReader(f):
            entity_type = f"CUSTOM_{row['type'].upper()}"
            escaped = row["value"].replace("\\", "\\\\").replace(".", "\\.")
            pattern = Pattern(name=f"{entity_type}:{row['value']}", regex=escaped, score=0.9)
            registry.add_recognizer(
                PatternRecognizer(
                    supported_entity=entity_type,
                    patterns=[pattern],
                    global_regex_flags=2,  # re.IGNORECASE
                )
            )

    extra_patterns = [
        ("PRIVATE_IPV4", r"\b(?:10\.\d{1,3}|172\.(?:1[6-9]|2\d|3[0-1])|192\.168)\.\d{1,3}\.\d{1,3}\b", 0.9),
        ("CIDR", r"\b(?:10|172|192)\.\d{1,3}\.\d{1,3}\.\d{1,3}/\d{1,2}\b", 0.9),
        ("WINDOWS_PATH", r"\b[A-Za-z]:\\(?:[^\\/:*?\"<>|\r\n]+\\)*[^\\/:*?\"<>|\r\n]*", 0.85),
        ("JWT", r"\beyJ[A-Za-z0-9_-]+\.[A-Za-z0-9_-]+\.[A-Za-z0-9_-]+\b", 0.95),
        ("PRIVATE_KEY", r"-----BEGIN [A-Z ]*PRIVATE KEY-----[\s\S]*?-----END [A-Z ]*PRIVATE KEY-----", 0.99),
        ("CONNECTION_STRING", r"\b(?:postgres|postgresql|mysql|mongodb)://[^\s]+", 0.95),
        ("URL", r"https?://[^\s)]+", 0.8),
    ]
    for entity, regex, score in extra_patterns:
        registry.add_recognizer(
            PatternRecognizer(
                supported_entity=entity,
                patterns=[Pattern(name=entity, regex=regex, score=score)],
                global_regex_flags=2,
            )
        )

    return registry


def main() -> int:
    registry = build_registry()
    analyzer = AnalyzerEngine(registry=registry, nlp_engine=None)
    anonymizer = AnonymizerEngine()
    corpus = json.loads(CORPUS.read_text(encoding="utf-8"))
    output = []

    for item in corpus:
        text = item["text"]
        results = analyzer.analyze(text=text, language="en")
        anonymized = anonymizer.anonymize(
            text=text,
            analyzer_results=results,
            operators={
                "DEFAULT": OperatorConfig("replace", {"new_value": "<REDACTED>"})
            },
        )
        output.append(
            {
                "id": item["id"],
                "findings": [
                    {
                        "entity_type": r.entity_type,
                        "start": r.start,
                        "end": r.end,
                        "score": round(float(r.score), 3),
                    }
                    for r in sorted(results, key=lambda r: (r.start, r.end, r.entity_type))
                ],
                "anonymized_text": anonymized.text,
            }
        )

    print(json.dumps(output, ensure_ascii=False, indent=2))
    return 0


if __name__ == "__main__":
    sys.exit(main())
