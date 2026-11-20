.PHONY: build test lint validate demo

build:
	dotnet build -c Release

test: build
	dotnet test -c Release --no-build
	cd analytics && python -m pytest

lint:
	cd analytics && ruff check . && ruff format --check . && mypy

validate:
	dotnet run --project src/BlackCanary.Cli -- validate fleet
	dotnet run --project src/BlackCanary.Cli -- validate demo/fleet

demo:
	python demo/run.py
