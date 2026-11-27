VAT = 0.19


def invoice_total(lines):
    net = sum(q * p for q, p in lines)
    return round(net * (1 + VAT), 2)
