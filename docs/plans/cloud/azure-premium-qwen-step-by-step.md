# Azure Premium Qwen Deployment, Step by Step

This is the clean-start checklist for hosting a premium Qwen worker for `Babel Player` on Azure.

It assumes:

- You deleted any earlier Azure scaffolding in the app repo.
- You are starting fresh.
- You want the recommended path: `Azure Container Apps` for the entitlement API and the hosted Qwen worker.

## One-Page Map

Use this if you just need the sequence before reading the details.

1. Install Azure CLI and sign in.
2. Confirm Container Apps GPU quota in your Azure region.
3. Create the base Azure resources.
4. Download the missing CUDA wheels into `inference/`.
5. Build the current worker image into ACR.
6. Stop.
7. Wait for repo changes: hosted-worker auth, entitlement API, desktop premium routing.
8. Resume with ACA environment creation, secrets, and app deployment.

## Conventions Used in This Doc

- `Portal path` means the exact place to click in Azure Portal.
- `CLI block` means you can copy and paste the commands directly after replacing placeholders.
- `Stop here` means do not continue until the condition is satisfied.
- `Recommended` means the default path I think makes the most sense for this repo.

## Current State

You are not blocked by deleting old Azure folders. This repo does not currently depend on a surviving `.azure/` scaffold for the path described here.

There are two important repo facts to understand before you do anything:

1. The app already has an external inference-host seam through `EffectiveContainerizedServiceUrl` in [AppSettings.cs](Services/Settings/AppSettings.cs).
2. The current FastAPI worker is not safe to expose publicly yet. The code explicitly warns that its endpoints are unauthenticated and intended for local or otherwise trusted callers in [inference/main.py](inference/main.py).

Because of that, this checklist is split into:

- Steps you can do right now
- The repo changes that still need to happen
- Steps to resume after those repo changes are in place

## Architecture

Recommended layout:

- `babel-entitlements-api`
  - CPU-only Azure Container App
  - Handles sign-in, entitlement checks, and short-lived token issuance
- `babel-qwen-worker`
  - GPU Azure Container App
  - Runs the FastAPI worker from [inference/main.py](inference/main.py)
  - Validates short-lived premium tokens on every Qwen request

Supporting Azure resources:

- Resource group
- Azure Container Registry
- Azure Key Vault
- Azure Storage Account + Azure Files share for model/cache persistence
- User-assigned managed identity
- Azure Container Apps environment

## Part 1: What You Can Do Right Now

### Step 0: Treat This as a Clean Start

Do not try to recover the deleted Azure folders. For this plan, they are not required.

### Step 1: Pick Naming and Region

Pick one naming scheme and one region and keep them consistent.

Example names:

- Resource group: `babel-player-rg`
- ACR: `babelplayeracr`
- Key Vault: `babel-player-kv`
- Storage account: `babelplayerstg`
- File share: `modelcache`
- Managed identity: `babel-player-uami`
- ACA environment: `babel-player-aca-env`

Write these down before you start clicking around. It prevents accidental naming drift later.

### Step 2: Install Local Tools

Required:

- Azure CLI

Recommended:

- Docker Desktop, if you want to test or build containers locally

Not required for this flow:

- `azd`

We are not using an `azd` or `.azure` workflow for the first pass.

#### Step 2 Checklist

- [ ] Azure CLI installed
- [ ] Docker Desktop installed if you want local container testing
- [ ] You know which Azure subscription you are using

### Step 3: Sign In and Prep Azure CLI

#### Portal path

- Azure Portal -> top search bar -> `Subscriptions`
- Confirm the subscription name you intend to use

#### CLI block

Run:

```powershell
az login
az account set --subscription "<your-subscription-id-or-name>"
az extension add --name containerapp --upgrade
az provider register --namespace Microsoft.App
az provider register --namespace Microsoft.ContainerRegistry
az provider register --namespace Microsoft.KeyVault
az provider register --namespace Microsoft.ManagedIdentity
az provider register --namespace Microsoft.Storage
```

#### Verify

Run this and confirm the subscription shown is the one you expect:

```powershell
az account show --query "{name:name,id:id}" -o table
```

### Step 4: Confirm GPU Quota Before Building Anything

This is the real blocker for the recommended path.

#### Portal path

1. Azure Portal -> `Subscriptions`
2. Click your target subscription
3. In the left nav, open `Usage + quotas`
4. Filter by your target region
5. Search for Container Apps or GPU-related quota entries
6. Confirm you can actually deploy a GPU-backed Container Apps workload there

If GPU quota is unavailable or denied:

- Stop here
- Do not continue with the ACA GPU worker plan
- Tell me, and we will keep Azure for the entitlement API and move only the Qwen worker to an Azure GPU VM

#### Step 4 Checklist

- [ ] Region chosen
- [ ] GPU quota confirmed in that region
- [ ] Decision made to continue with ACA GPU instead of VM fallback

### Step 5: Create the Core Azure Resources

#### CLI block

After quota is confirmed, run:

```powershell
$RG="babel-player-rg"
$LOC="<gpu-supported-region>"
$ACR="babelplayeracr"
$KV="babel-player-kv"
$ST="babelplayerstg"
$FS="modelcache"
$MI="babel-player-uami"

az group create -n $RG -l $LOC
az acr create -n $ACR -g $RG --sku Basic
az keyvault create -n $KV -g $RG -l $LOC
az storage account create -n $ST -g $RG -l $LOC --sku Standard_LRS --kind StorageV2
az storage share-rm create --resource-group $RG --storage-account $ST --name $FS --quota 200
az identity create -g $RG -n $MI
```

#### Verify

Run:

```powershell
az group show -n $RG --query "{name:name,location:location}" -o table
az acr show -n $ACR -g $RG --query "{name:name,loginServer:loginServer}" -o table
az keyvault show -n $KV --query "{name:name,location:location}" -o table
az storage account show -n $ST -g $RG --query "{name:name,primaryLocation:primaryLocation}" -o table
az identity show -g $RG -n $MI --query "{name:name,principalId:principalId}" -o table
```

#### Step 5 Checklist

- [ ] Resource group created
- [ ] ACR created
- [ ] Key Vault created
- [ ] Storage account created
- [ ] Azure Files share created
- [ ] User-assigned managed identity created

### Step 6: Grant the Managed Identity Access

The user-assigned managed identity needs to:

- Pull from ACR
- Read secrets from Key Vault

#### Portal path

If you prefer clicks instead of CLI:

1. Azure Portal -> `Container registries` -> your ACR -> `Access control (IAM)` -> `Add role assignment`
2. Assign `AcrPull` to your user-assigned managed identity
3. Azure Portal -> `Key vaults` -> your Key Vault -> `Access control (IAM)` -> `Add role assignment`
4. Assign `Key Vault Secrets User` to the same identity

#### CLI block

Run:

```powershell
$MI_PRINCIPAL = az identity show -g $RG -n $MI --query principalId -o tsv
$ACR_ID = az acr show -n $ACR -g $RG --query id -o tsv
$KV_ID = az keyvault show -n $KV -g $RG --query id -o tsv

az role assignment create --assignee-object-id $MI_PRINCIPAL --assignee-principal-type ServicePrincipal --role AcrPull --scope $ACR_ID
az role assignment create --assignee-object-id $MI_PRINCIPAL --assignee-principal-type ServicePrincipal --role "Key Vault Secrets User" --scope $KV_ID
```

#### Verify

Run:

```powershell
az role assignment list --assignee-object-id $MI_PRINCIPAL --scope $ACR_ID --query "[].roleDefinitionName" -o table
az role assignment list --assignee-object-id $MI_PRINCIPAL --scope $KV_ID --query "[].roleDefinitionName" -o table
```

### Step 7: Download the CUDA Wheels the Worker Image Expects

The current worker image will not build until these files exist in `inference/`.

The Dockerfile explicitly expects them at [inference/Dockerfile](inference/Dockerfile).

Run:

```powershell
curl.exe -L -o inference/torch-2.8.0+cu128.whl "https://download.pytorch.org/whl/cu128/torch-2.8.0%2Bcu128-cp310-cp310-manylinux_2_28_x86_64.whl"
curl.exe -L -o inference/torchaudio-2.8.0+cu128.whl "https://download.pytorch.org/whl/cu128/torchaudio-2.8.0%2Bcu128-cp310-cp310-manylinux_2_28_x86_64.whl"
curl.exe -L -o inference/torchvision-0.23.0+cu128.whl "https://download.pytorch.org/whl/cu128/torchvision-0.23.0%2Bcu128-cp310-cp310-manylinux_2_28_x86_64.whl"
```

#### Verify

Run:

```powershell
Get-ChildItem inference\*.whl
```

You should see exactly these three wheel files in `inference/`.

### Step 8: Build the Worker Image Into ACR

Build the image now, but do not expose the worker publicly yet.

#### CLI block

```powershell
az acr build --registry $ACR --image babel-qwen-worker:preauth .\inference
```

#### Verify

Run:

```powershell
az acr repository show-tags --name $ACR --repository babel-qwen-worker -o table
```

### Step 9: Stop Here for the Worker

Do not create a public Azure Container App for the current worker yet.

Reason:

- The worker is currently designed for local/private use
- The code itself says non-loopback hosting is unsafe without auth at [inference/main.py](inference/main.py)

At this point, your Azure-side work is paused until the repo gets the missing auth and entitlement pieces.

#### Stop Condition

Do not proceed to ACA deployment until all three of these are true:

- [ ] Hosted-worker auth exists
- [ ] Entitlement API exists
- [ ] Desktop app can route premium traffic to the hosted worker

## Part 2: Repo Changes Still Needed

These pieces still need to be added before the hosted premium path is safe and usable.

### Step 10: Add Auth to the Hosted Qwen Worker

The worker needs request authentication in front of the Qwen endpoints.

That means:

- Validating a short-lived JWT or equivalent premium token
- Rejecting unauthorized requests before any model work begins

### Step 11: Add a Small Entitlement API

The entitlement API should:

- Verify the user is premium
- Issue short-lived signed tokens
- Return enough metadata for the desktop app to call the hosted worker

### Step 12: Add Desktop App Support for Hosted Premium Qwen

The desktop app needs a premium/cloud Qwen path that is distinct from the local GPU host.

Do not silently repoint local Qwen to the cloud.

The UI and app flow should keep these separate:

- `Qwen3-TTS (Local GPU Host)`
- `Qwen3-TTS (Premium Cloud)`

## Part 3: Resume After Repo Changes Are In Place

Once the auth, entitlement API, and desktop integration exist, continue here.

### Step 13: Put the JWT Secrets Into Key Vault

Create Key Vault secrets for at least:

- `premium-jwt-signing-key`
- `premium-jwt-issuer`
- `premium-jwt-audience`

You can add more later if billing or identity providers need them.

#### Portal path

1. Azure Portal -> `Key vaults` -> your Key Vault
2. Left nav -> `Objects` -> `Secrets`
3. Click `Generate/Import`
4. Repeat for each secret name

#### CLI block

```powershell
az keyvault secret set --vault-name $KV --name premium-jwt-signing-key --value "<long-random-secret>"
az keyvault secret set --vault-name $KV --name premium-jwt-issuer --value "<issuer-value>"
az keyvault secret set --vault-name $KV --name premium-jwt-audience --value "<audience-value>"
```

### Step 14: Create the ACA Environment

Create the Azure Container Apps environment in the region where your GPU quota is available.

#### Portal path

For the first run, use the portal:

1. Azure Portal -> top search -> `Container Apps`
2. Click `Environments`
3. Click `Create`
4. Choose:
   - your resource group
   - your confirmed GPU-capable region
   - environment name such as `babel-player-aca-env`
5. On workload profile or environment sizing screens, select the option that supports your planned GPU worker
6. Complete the create flow

For the first run, the portal is better than forcing a half-guessed CLI command because you can visually confirm:

- Region
- Workload profile
- Ingress settings
- Identity attachment

#### Step 14 Checklist

- [ ] ACA environment created
- [ ] Correct region selected
- [ ] Environment is the one you will deploy both apps into

### Step 15: Deploy `babel-entitlements-api`

Deploy the entitlement API as a normal CPU Container App.

Requirements:

- User-assigned managed identity attached
- Key Vault secret references for JWT settings
- External ingress enabled

#### Portal path

1. Azure Portal -> `Container Apps`
2. Click `Create`
3. Choose:
   - resource group: your existing RG
   - container app environment: the ACA environment from Step 14
   - app name: `babel-entitlements-api`
4. On the container page:
   - choose image source: `Azure Container Registry`
   - pick your ACR image for the entitlement API
5. On identity:
   - attach the user-assigned managed identity
6. On ingress:
   - enable external ingress
7. On secrets / environment:
   - use Key Vault references for JWT secrets

#### CLI notes

Do not finalize the entitlement API deployment command until the repo-side API exists and its exact image name, secret names, and environment variable names are finalized.

### Step 16: Deploy `babel-qwen-worker`

Deploy the Qwen worker as the GPU Container App.

Requirements:

- User-assigned managed identity attached
- ACR pull via managed identity
- External ingress enabled only after auth is in place
- Azure Files mount for model cache persistence

The worker image exposes port `8001` and its health endpoint is `/health/live` according to [inference/Dockerfile](inference/Dockerfile).

#### Portal path

1. Azure Portal -> `Container Apps`
2. Click `Create`
3. Choose:
   - resource group: your existing RG
   - environment: the ACA environment from Step 14
   - app name: `babel-qwen-worker`
4. On the container page:
   - image source: `Azure Container Registry`
   - image: `babel-qwen-worker:preauth` at first, then switch to the authenticated image after repo changes land
   - target port: `8001`
5. On identity:
   - attach the user-assigned managed identity
6. On ingress:
   - do not expose this publicly until the authenticated worker image exists
7. On volumes:
   - mount the Azure Files share for model/cache persistence

#### CLI notes

The final worker deployment command should be generated after the auth-enabled worker image exists, because the stable environment variables, secrets, and mount paths need to match the final repo implementation.

### Step 17: Mount Persistent Storage for the Worker

Use the Azure Files share so the worker does not redownload or rebuild everything on every restart.

Persist at least:

- Hugging Face cache
- Model download/cache directories
- Any worker temp or output directories you want to survive restarts

#### Portal path

1. Azure Portal -> `Container Apps` -> your worker app
2. Open `Revisions and replicas` or the storage/volume section available in the current portal layout
3. Add an Azure Files-backed volume
4. Mount it into the worker container at the cache path you choose

#### Recommended persisted paths

- Hugging Face cache root
- Model weights directory
- Any audio/output temp path that should survive restarts

### Step 18: Point the Desktop App at the Hosted Worker

After the repo changes exist, wire the desktop app to the hosted worker using the existing external-host seam in [AppSettings.cs](Services/Settings/AppSettings.cs).

This should be explicit and user-visible, not a silent redirect from the local provider.

#### Repo seam

The current seam for an external host already exists in [AppSettings.cs](Services/Settings/AppSettings.cs). The premium/cloud flow should use that seam after the repo-side premium support is added.

### Step 19: Test the Full Premium Flow

Verify all of the following:

- Premium user can request a hosted Qwen action
- Desktop app receives a valid short-lived token
- Worker accepts the token
- Non-premium user is rejected
- Health and capability endpoints behave as expected

#### Minimum smoke test order

1. Confirm entitlement API health
2. Confirm worker health
3. Confirm worker rejects unauthenticated premium requests
4. Confirm worker accepts authenticated premium requests
5. Confirm the desktop app can complete one premium Qwen operation end to end

### Step 20: Only Then Add Extras

After the core path works, you can layer on:

- Custom domain
- Secret rotation
- Rate limiting
- Better billing integration
- Autoscaling tuning
- Front Door or API Management

Do not do these first.

## Useful Notes

### Why We Are Not Reusing the Old Azure Folders

The current recommended path is not based on recovering old `azd` scaffolding. The repo already has the right service boundary for a hosted inference worker. The missing pieces are auth and entitlement, not old infra files.

### Why the Worker Is Paused at Step 9

Because the current worker is intentionally loopback-safe by default and not authenticated for public hosting.

That warning is not theoretical. The worker exposes file uploads, model warmup, transcription, translation, and Qwen TTS operations, so it should not be internet-facing until auth is in place.

### Why ACA Is Still the Recommended First Choice

`Azure Container Apps` is still the cleanest fit for this repo because:

- The worker is already a containerized FastAPI service
- ACA gives you ingress, identity, revisions, and registry integration without running a VM yourself
- You can keep the entitlement API and worker in one managed environment

If ACA GPU quota blocks you, the fallback is not to throw away the plan. It is to keep the same architecture and move only the worker to a GPU VM.

## Reference Files in This Repo

- External inference host seam: [AppSettings.cs](Services/Settings/AppSettings.cs)
- Worker Docker image: [inference/Dockerfile](inference/Dockerfile)
- Worker safety note for non-loopback bind: [inference/main.py](inference/main.py)
- Container/inference host overview: [docs/containers.md](docs/containers.md)

## Current Recommended Next Move

Do `Part 1` through `Step 8`, then stop.

At that point, the next action is repo work:

- add hosted-worker auth
- add entitlement API
- add desktop-side premium routing

After those are in place, continue with `Part 3`.

## Copy-Paste CLI Blocks by Section

This section is just the command groups, without explanation.

### CLI Group A: Azure CLI Setup

```powershell
az login
az account set --subscription "<your-subscription-id-or-name>"
az extension add --name containerapp --upgrade
az provider register --namespace Microsoft.App
az provider register --namespace Microsoft.ContainerRegistry
az provider register --namespace Microsoft.KeyVault
az provider register --namespace Microsoft.ManagedIdentity
az provider register --namespace Microsoft.Storage
az account show --query "{name:name,id:id}" -o table
```

### CLI Group B: Create Core Azure Resources

```powershell
$RG="babel-player-rg"
$LOC="<gpu-supported-region>"
$ACR="babelplayeracr"
$KV="babel-player-kv"
$ST="babelplayerstg"
$FS="modelcache"
$MI="babel-player-uami"

az group create -n $RG -l $LOC
az acr create -n $ACR -g $RG --sku Basic
az keyvault create -n $KV -g $RG -l $LOC
az storage account create -n $ST -g $RG -l $LOC --sku Standard_LRS --kind StorageV2
az storage share-rm create --resource-group $RG --storage-account $ST --name $FS --quota 200
az identity create -g $RG -n $MI
```

### CLI Group C: Assign Identity Permissions

```powershell
$MI_PRINCIPAL = az identity show -g $RG -n $MI --query principalId -o tsv
$ACR_ID = az acr show -n $ACR -g $RG --query id -o tsv
$KV_ID = az keyvault show -n $KV --query id -o tsv

az role assignment create --assignee-object-id $MI_PRINCIPAL --assignee-principal-type ServicePrincipal --role AcrPull --scope $ACR_ID
az role assignment create --assignee-object-id $MI_PRINCIPAL --assignee-principal-type ServicePrincipal --role "Key Vault Secrets User" --scope $KV_ID
```

### CLI Group D: Download Required CUDA Wheels

```powershell
curl.exe -L -o inference/torch-2.8.0+cu128.whl "https://download.pytorch.org/whl/cu128/torch-2.8.0%2Bcu128-cp310-cp310-manylinux_2_28_x86_64.whl"
curl.exe -L -o inference/torchaudio-2.8.0+cu128.whl "https://download.pytorch.org/whl/cu128/torchaudio-2.8.0%2Bcu128-cp310-cp310-manylinux_2_28_x86_64.whl"
curl.exe -L -o inference/torchvision-0.23.0+cu128.whl "https://download.pytorch.org/whl/cu128/torchvision-0.23.0%2Bcu128-cp310-cp310-manylinux_2_28_x86_64.whl"
Get-ChildItem inference\*.whl
```

### CLI Group E: Build Worker Image Into ACR

```powershell
az acr build --registry $ACR --image babel-qwen-worker:preauth .\inference
az acr repository show-tags --name $ACR --repository babel-qwen-worker -o table
```

### CLI Group F: Add JWT Secrets Later

```powershell
az keyvault secret set --vault-name $KV --name premium-jwt-signing-key --value "<long-random-secret>"
az keyvault secret set --vault-name $KV --name premium-jwt-issuer --value "<issuer-value>"
az keyvault secret set --vault-name $KV --name premium-jwt-audience --value "<audience-value>"
```
