"""The host application every arm talks to: a small shop API over fixtures/shop.json.

The tools are defined once, as JSON Schema specs. Arm A exposes them as the `shop` program on Topwater, arm B as
separate function-calling tools, arm C as the `shop` program in a container (which calls back into this code).
One Shop instance per episode, so its state (refunds, purchase orders, the flaky backends) starts fresh.
"""
import copy
import json
import re
from pathlib import Path

DATA = json.loads((Path(__file__).parent.parent / "fixtures" / "shop.json").read_text())


def _page_props():
    return {"page": {"type": "integer", "description": "Page number, starting at 1 (default 1)."},
            "page_size": {"type": "integer", "description": "Results per page, at most 50 (default 20)."}}


def _obj(props, required=()):
    return {"type": "object", "properties": props, "required": list(required)}


SPECS = [
    ("list_orders", "List orders, newest first, one page at a time. Each order has id, customer_id, placed_on (YYYY-MM-DD), "
                    "status, total (EUR) and items (sku, qty, unit_price). The response gives page, total_pages and total_count.",
     _obj({"status": {"type": "string", "enum": ["open", "shipped", "delivered", "cancelled", "refunded"], "description": "Only orders with this status."},
           "customer_id": {"type": "string", "description": "Only this customer's orders, e.g. C-0007."},
           "placed_after": {"type": "string", "description": "Only orders placed on or after this date (YYYY-MM-DD)."},
           "placed_before": {"type": "string", "description": "Only orders placed on or before this date (YYYY-MM-DD)."},
           **_page_props()})),
    ("get_order", "Get one order by id (e.g. O-1042).", _obj({"order_id": {"type": "string", "description": "The order id."}}, ["order_id"])),
    ("list_customers", "List customers, one page at a time: id, name, country (ISO code), tier (bronze, silver, gold), email, joined.",
     _obj({"country": {"type": "string", "description": "Only customers in this country (two-letter code, e.g. DE)."},
           "tier": {"type": "string", "enum": ["bronze", "silver", "gold"], "description": "Only customers of this tier."},
           **_page_props()})),
    ("get_customer", "Get one customer by id.", _obj({"customer_id": {"type": "string", "description": "The customer id."}}, ["customer_id"])),
    ("list_products", "List all products: sku, name, category, price (EUR) and reorder_point (the minimum total stock across all warehouses).",
     _obj({"category": {"type": "string", "description": "Only products in this category (tools, garden, kitchen, lighting)."}})),
    ("get_inventory", "Stock on hand per product and warehouse (BER, LYO, NYC).",
     _obj({"sku": {"type": "string", "description": "Only this product."}, "warehouse": {"type": "string", "description": "Only this warehouse."}})),
    ("list_shipments", "List shipments, one page at a time: id, order_id, carrier, shipped_on, status (in_transit, delivered).",
     _obj({"carrier": {"type": "string", "description": "Only this carrier."}, "order_id": {"type": "string", "description": "Only this order's shipments."},
           **_page_props()})),
    ("request_sales_report", "Start building the monthly sales report. Returns a job id; fetch the result with get_report.",
     _obj({"month": {"type": "string", "description": "The month, YYYY-MM."}}, ["month"])),
    ("get_report", "Get a report job's status, and the report once it is done.", _obj({"job_id": {"type": "string", "description": "The job id."}}, ["job_id"])),
    ("create_refund", "Refund an order (all or part of it). Only shipped or delivered orders can be refunded.",
     _obj({"order_id": {"type": "string", "description": "The order to refund."},
           "amount": {"type": "number", "description": "Amount in EUR, at most the order total."},
           "reason": {"type": "string", "description": "Why the order is refunded."}}, ["order_id", "amount", "reason"])),
    ("create_purchase_order", "Order stock from the supplier for one product, delivered to one warehouse.",
     _obj({"sku": {"type": "string", "description": "The product."}, "warehouse": {"type": "string", "description": "BER, LYO or NYC."},
           "quantity": {"type": "integer", "description": "Units to order, at least 1."}}, ["sku", "warehouse", "quantity"])),
]
GROUP_DESCRIPTION = "The shop's order, customer, inventory, shipment, refund and report API"


class ShopError(Exception):
    pass


class Shop:
    def __init__(self):
        self.db = copy.deepcopy(DATA)
        self.refunds, self.purchase_orders, self.calls = [], [], []
        self.shipments_warm = False
        self.jobs = {}

    def invoke(self, name: str, args: dict):
        """Returns (text, is_error). The text is the JSON response, or the error message."""
        self.calls.append(name)
        try:
            return json.dumps(getattr(self, name)(**args)), False
        except ShopError as e:
            return str(e), True
        except TypeError as e:  # unknown or missing argument
            return f"bad arguments: {e}", True

    @staticmethod
    def _page(rows, key, page=1, page_size=20):
        if not isinstance(page, int) or page < 1:
            raise ShopError("page must be 1 or more")
        if not isinstance(page_size, int) or not 1 <= page_size <= 50:
            raise ShopError("page_size must be between 1 and 50")
        pages = max(1, -(-len(rows) // page_size))
        return {"page": page, "total_pages": pages, "total_count": len(rows), key: rows[(page - 1) * page_size: page * page_size]}

    @staticmethod
    def _date(s, name):
        if s is not None and not re.fullmatch(r"\d{4}-\d{2}-\d{2}", s):
            raise ShopError(f"{name} must be a date like 2026-10-01")
        return s

    def list_orders(self, status=None, customer_id=None, placed_after=None, placed_before=None, page=1, page_size=20):
        self._date(placed_after, "placed_after"), self._date(placed_before, "placed_before")
        rows = [o for o in reversed(self.db["orders"])
                if (status is None or o["status"] == status) and (customer_id is None or o["customer_id"] == customer_id)
                and (placed_after is None or o["placed_on"] >= placed_after) and (placed_before is None or o["placed_on"] <= placed_before)]
        return self._page(rows, "orders", page, page_size)

    def get_order(self, order_id):
        for o in self.db["orders"]:
            if o["id"] == order_id:
                return o
        raise ShopError(f"order '{order_id}' not found")

    def list_customers(self, country=None, tier=None, page=1, page_size=20):
        rows = [c for c in self.db["customers"] if (country is None or c["country"] == country) and (tier is None or c["tier"] == tier)]
        return self._page(rows, "customers", page, page_size)

    def get_customer(self, customer_id):
        if not re.fullmatch(r"C-\d{4}", str(customer_id)):
            raise ShopError(f"invalid customer id '{customer_id}': customer ids look like C-0001")
        for c in self.db["customers"]:
            if c["id"] == customer_id:
                return c
        raise ShopError(f"customer '{customer_id}' not found")

    def list_products(self, category=None):
        return [p for p in self.db["products"] if category is None or p["category"] == category]

    def get_inventory(self, sku=None, warehouse=None):
        return [i for i in self.db["inventory"] if (sku is None or i["sku"] == sku) and (warehouse is None or i["warehouse"] == warehouse)]

    def list_shipments(self, carrier=None, order_id=None, page=1, page_size=20):
        if not self.shipments_warm:  # the first call of every episode fails, as a cold backend would
            self.shipments_warm = True
            raise ShopError("503 Service Unavailable: the shipments backend is starting up, try again")
        rows = [s for s in self.db["shipments"] if (carrier is None or s["carrier"] == carrier) and (order_id is None or s["order_id"] == order_id)]
        return self._page(rows, "shipments", page, page_size)

    def request_sales_report(self, month):
        if not re.fullmatch(r"2026-(0[1-9]|1[0-2])", str(month)):
            raise ShopError("month must look like 2026-10 (only 2026 is available)")
        job = f"R-{len(self.jobs) + 1}"
        self.jobs[job] = {"month": month, "polls": 0}
        return {"job_id": job, "status": "queued"}

    def get_report(self, job_id):
        job = self.jobs.get(job_id)
        if job is None:
            raise ShopError(f"no report job '{job_id}'")
        job["polls"] += 1
        if job["polls"] < 3:  # takes a couple of polls
            return {"job_id": job_id, "status": "running"}
        return {"job_id": job_id, "status": "done", "report": sales_report(self.db, job["month"])}

    def create_refund(self, order_id, amount, reason):
        o = self.get_order(order_id)
        if o["status"] not in ("shipped", "delivered"):
            raise ShopError(f"order {order_id} is {o['status']}; only shipped or delivered orders can be refunded")
        if not isinstance(amount, (int, float)) or amount <= 0 or amount > o["total"] + 1e-9:
            raise ShopError(f"amount must be more than 0 and at most the order total ({o['total']})")
        self.refunds.append({"order_id": order_id, "amount": float(amount), "reason": reason})
        return {"refund_id": f"RF-{len(self.refunds)}", "order_id": order_id, "amount": amount, "status": "issued"}

    def create_purchase_order(self, sku, warehouse, quantity):
        if not any(p["sku"] == sku for p in self.db["products"]):
            raise ShopError(f"unknown sku '{sku}'")
        if warehouse not in ("BER", "LYO", "NYC"):
            raise ShopError("warehouse must be BER, LYO or NYC")
        if not isinstance(quantity, int) or quantity < 1:
            raise ShopError("quantity must be a whole number, at least 1")
        self.purchase_orders.append({"sku": sku, "warehouse": warehouse, "quantity": quantity})
        return {"po_id": f"PO-{len(self.purchase_orders)}", "sku": sku, "warehouse": warehouse, "quantity": quantity, "status": "placed"}


def sales_report(db, month):
    rows = [o for o in db["orders"] if o["placed_on"].startswith(month) and o["status"] != "cancelled"]
    gross = round(sum(o["total"] for o in rows), 2)
    refunds = round(sum(o["total"] for o in rows if o["status"] == "refunded"), 2)
    return {"month": month, "orders": len(rows), "gross_revenue": gross, "refunded": refunds, "net_revenue": round(gross - refunds, 2)}
