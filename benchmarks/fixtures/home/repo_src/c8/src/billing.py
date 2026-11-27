VAT = 0.19


def invoice_total(lines, discount=0.0):
    net = sum(q * p for q, p in lines) * (1 - discount)
    return round(net * (1 + VAT), 2)


def on_paid(invoice):
    # customers recieve a receipt
    return f'paid {invoice}'


def retry(payment, attempts):
    return [payment] * attempts
