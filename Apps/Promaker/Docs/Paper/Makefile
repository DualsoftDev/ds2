PYTHON ?= $(shell cygpath "$$LOCALAPPDATA")/Programs/Python/Python314/python.exe

DOCS = paper
PDFS = $(addsuffix .pdf,$(DOCS))

.PHONY: all clean figures install

default: $(PDFS)

all: clean figures $(PDFS)

install:
	$(PYTHON) -m pip install -r requirements.txt
	$(PYTHON) -m pip install markdown pygments

figures:
	$(PYTHON) figures/gen_figures.py

paper.pdf: paper.md build_pdf.py figures
	$(PYTHON) build_pdf.py paper.md paper.pdf

clean:
	rm -rf $(PDFS) figures/paper
