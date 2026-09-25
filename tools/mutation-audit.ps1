# Re-runs every mutation referenced in the documentation against the CURRENT code.
# Each mutation is applied, the relevant tests are run, and the source is restored
# whether or not the run succeeds. Prints a table of what actually failed.

$ErrorActionPreference = 'Continue'
$root = 'C:\Projects\OrderProcessingSystem'
Set-Location $root

$mutations = @(
  @{
    Name    = 'Customers granted the admin cancellation window'
    File    = 'src\OrderProcessing.Domain\Orders\OrderStatusTransitions.cs'
    Find    = '[(ActorType.Customer, OrderStatus.Processing)] = Empty,'
    Replace = '[(ActorType.Customer, OrderStatus.Processing)] = Set(OrderStatus.Cancelled),'
    Filter  = 'FullyQualifiedName~OrderProcessing.Domain.Tests'
  },
  @{
    Name    = 'Claim drops the lease guard'
    File    = 'src\OrderProcessing.Infrastructure\Scheduling\SqlitePendingOrderClaimer.cs'
    Find    = 'AND "Status" = $pendingStatus
        AND "PromotionLease" IS NULL
        RETURNING "Id";'
    Replace = 'AND "Status" = $pendingStatus
        RETURNING "Id";'
    Filter  = 'FullyQualifiedName~OrderProcessing.Integration.Tests'
  },
  @{
    Name    = 'Claim drops the pending-status filter'
    File    = 'src\OrderProcessing.Infrastructure\Scheduling\SqlitePendingOrderClaimer.cs'
    Find    = '            WHERE "Status" = $pendingStatus
              AND "PromotionLease" IS NULL'
    Replace = '            WHERE "PromotionLease" IS NULL'
    Filter  = 'FullyQualifiedName~OrderProcessing.Integration.Tests'
  },
  @{
    Name    = 'Claim no longer requires an enclosing transaction'
    File    = 'src\OrderProcessing.Infrastructure\Scheduling\SqlitePendingOrderClaimer.cs'
    Find    = 'var transaction = dbContext.Database.CurrentTransaction
            ?? throw new InvalidOperationException('
    Replace = 'var transaction = dbContext.Database.CurrentTransaction
            ?? DummyNeverThrows('
    Filter  = 'FullyQualifiedName~OrderProcessing.Integration.Tests'
    Skip    = $true   # would not compile; the transaction requirement is a type-level guard
  },
  @{
    Name    = 'Promotion bypasses the transition matrix'
    File    = 'src\OrderProcessing.Domain\Orders\Order.cs'
    Find    = 'OrderStatusTransitions.EnsureAllowed(actor.Type, Status, newStatus);'
    Replace = '// matrix check removed'
    Filter  = $null
  },
  @{
    Name    = 'Order currency reverts to "first line wins"'
    File    = 'src\OrderProcessing.Domain\Orders\Order.cs'
    Find    = 'return currencies.Count == 1
            ? currencies[0]
            : throw new MixedCurrencyOrderException(currencies);'
    Replace = 'return currencies[0];'
    Filter  = $null
  },
  @{
    Name    = 'Unique-violation translation disabled for order numbers'
    File    = 'src\OrderProcessing.Infrastructure\Persistence\EfOrderRepository.cs'
    Find    = 'catch (DbUpdateException ex) when (ViolatesUniqueIndexOn(ex, nameof(Order.OrderNumber)))'
    Replace = 'catch (DbUpdateException ex) when (false && ViolatesUniqueIndexOn(ex, nameof(Order.OrderNumber)))'
    Filter  = 'FullyQualifiedName~OrderProcessing.Integration.Tests'
  },
  @{
    Name    = 'Ownership scoping removed (customers see every order)'
    File    = 'src\OrderProcessing.Infrastructure\Persistence\EfOrderRepository.cs'
    Find    = 'scope.IsUnrestricted
            ? source
            : source.Where(order => order.CustomerId == scope.CustomerId);'
    Replace = 'source;'
    Filter  = 'FullyQualifiedName~OrderProcessing.Integration.Tests'
  }
)

$results = @()

foreach ($m in $mutations) {
  if ($m.Skip) {
    $results += [pscustomobject]@{ Mutation = $m.Name; Failed = 'skipped'; Note = 'would not compile' }
    continue
  }

  $path = Join-Path $root $m.File
  $original = Get-Content $path -Raw

  if (-not $original.Contains($m.Find)) {
    $results += [pscustomobject]@{ Mutation = $m.Name; Failed = 'NOT APPLIED'; Note = 'anchor not found' }
    continue
  }

  Set-Content $path $original.Replace($m.Find, $m.Replace) -NoNewline

  try {
    $args = @('test', '--nologo')
    if ($m.Filter) { $args += @('--filter', $m.Filter) }
    $output = & dotnet @args 2>&1 | Out-String

    $failed = ([regex]::Matches($output, 'Failed:\s+(\d+)') | ForEach-Object { [int]$_.Groups[1].Value } | Measure-Object -Sum).Sum
    $note = if ($output -match 'error CS') { 'compile error' } else { '' }
  }
  finally {
    Set-Content $path $original -NoNewline
  }

  $results += [pscustomobject]@{ Mutation = $m.Name; Failed = $failed; Note = $note }
}

Write-Host "`n=== Mutation audit (current code) ==="
$results | Format-Table -AutoSize -Wrap

Write-Host "`nVerifying all sources restored..."
$verify = & dotnet test --nologo 2>&1 | Out-String
if ($verify -match 'Failed!') { Write-Host "  RESTORE FAILED - suite is red" } else { Write-Host "  restored; suite green" }
