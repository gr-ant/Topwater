def invoice_total(lines):
    return sum(q * p for q, p in lines)
