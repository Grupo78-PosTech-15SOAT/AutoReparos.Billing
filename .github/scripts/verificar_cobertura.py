#!/usr/bin/env python3
"""Falha quando a cobertura de linhas, unida entre os relatórios Cobertura, fica abaixo do mínimo.

Uso: python3 .github/scripts/verificar_cobertura.py <pasta-de-resultados> --minimo 80
Uma linha conta como coberta se algum relatório registrar hits > 0 para ela, então
assemblies medidos por mais de um projeto de teste não são contados em dobro.
"""
import argparse
import glob
import os
import sys
import xml.etree.ElementTree as ET


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("pasta")
    parser.add_argument("--minimo", type=float, required=True)
    args = parser.parse_args()

    arquivos = glob.glob(os.path.join(args.pasta, "**", "coverage.cobertura.xml"), recursive=True)
    if not arquivos:
        print(f"::error::Nenhum coverage.cobertura.xml em {args.pasta}")
        return 1

    linhas = {}
    for arquivo in arquivos:
        raiz = ET.parse(arquivo).getroot()
        for classe in raiz.iter("class"):
            nome = classe.get("filename", "")
            for linha in classe.iter("line"):
                chave = (nome, linha.get("number"))
                coberta = int(linha.get("hits", "0")) > 0
                linhas[chave] = linhas.get(chave, False) or coberta

    total = len(linhas)
    if total == 0:
        print("::error::Os relatórios de cobertura não têm linhas")
        return 1
    cobertas = sum(1 for valor in linhas.values() if valor)
    percentual = 100.0 * cobertas / total
    print(f"Cobertura de linhas: {percentual:.2f}% ({cobertas}/{total}) em {len(arquivos)} relatório(s); mínimo {args.minimo:.0f}%")
    if percentual < args.minimo:
        print(f"::error::Cobertura de linhas {percentual:.2f}% abaixo do mínimo de {args.minimo:.0f}%")
        return 1
    return 0


if __name__ == "__main__":
    sys.exit(main())
