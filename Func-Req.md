Objective:
The goal is to design, implement, and test a backend system in .Net language that handles order processing efficiently.
Scenario:
You have been hired to build the backend for an E-commerce Order Processing System. The system should allow customers to place orders, track their status, and support basic order operations.
Requirements
1. Core Features
  •  Create an order: Customers should be able to place an order with multiple items.
  •  Retrieve order details: The system should allow fetching order details by order ID.
  •  Update order status: The order should have statuses like PENDING, PROCESSING, SHIPPED, and DELIVERED. A background job should automatically update PENDING orders to PROCESSING every 5 minutes.
  •  List all orders: Retrieve all orders, optionally filtered by status.
  •  Cancel an order: Customers should be able to cancel an order, but only if it’s still in PENDING status.
