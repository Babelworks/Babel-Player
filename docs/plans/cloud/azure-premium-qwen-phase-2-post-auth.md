# Azure Premium Qwen, Phase 2

This document covers the deployment phase that starts **after** the repo has all three of these:

- hosted-worker auth
- entitlement API
- desktop-side premium routing

Until those land, treat this as a staging checklist with placeholders.

## Status

This is a **draft deployment runbook**.

It is intentionally written so you can prepare the Azure side now, but some values are still placeholders until the repo implementation is finished.

## What This Phase Assumes

You already completed [azure-premium-qwen-step-by-step.md](azure-premium-qwen-step-by-step.md) through Step 8.

That means all of this should already exist:

- resource group
- Azure Container Registry
- Key Vault
- storage account
- Azure Files share
- user-assigned managed identity
- worker base image in ACR

## Final Goal

By the end of this phase, Azure should have:

- `babel-entitlements-api`
  - CPU-only Azure Container App
  - external ingress enabled
  - managed identity attached
  - Key Vault-backed JWT settings
- `babel-qwen-worker`
  - GPU Azure Container App
  - ingress enabled only after auth-enabled image is ready
  - managed identity attached
  - Azure Files volume mounted for model/cache persistence
- desktop app configured to call the hosted worker explicitly for premium Qwen

## Prerequisites Checklist

Do not start this phase until all boxes are true.

- [ ] GPU quota is confirmed for your ACA region
- [ ] Base Azure resources exist
- [ ] ACR already contains your worker image
- [ ] Hosted-worker auth exists in the repo
- [ ] Entitlement API exists in the repo
- [ ] Desktop premium routing exists in the repo
- [ ] Final image names are known
- [ ] Final environment variable names are known

## Values You Will Need

Fill these in once the repo-side implementation is done.

```text
Subscription:
Resource group:
Region:
ACA environment name:
ACR login server:
Managed identity resource ID:
Managed identity client ID:
Key Vault name:
Storage account name:
Azure Files share name:

Entitlement API image:
Entitlement API target port:
Entitlement API environment variables:

Worker image:
Worker target port: 8001
Worker environment variables:
Worker cache mount path:
Worker output/temp mount path:
```

## Phase 2 Sequence

1. Finalize JWT and entitlement secrets in Key Vault.
2. Create the ACA environment if it does not already exist.
3. Deploy the entitlement API Container App.
4. Deploy the auth-enabled Qwen worker Container App.
5. Mount persistent storage into the worker.
6. Validate health, auth rejection, and auth success.
7. Point the desktop app at the hosted premium worker.
8. Run one end-to-end premium smoke test.

## Step 1: Finalize Key Vault Secrets

At minimum, create:

- `premium-jwt-signing-key`
- `premium-jwt-issuer`
- `premium-jwt-audience`

You may also need additional secrets depending on your billing or identity provider.

### Portal path

1. Azure Portal -> `Key vaults`
2. Open your Key Vault
3. Left nav -> `Objects` -> `Secrets`
4. Click `Generate/Import`
5. Add each secret one at a time

### CLI template

```powershell
$KV="<key-vault-name>"

az keyvault secret set --vault-name $KV --name premium-jwt-signing-key --value "<long-random-secret>"
az keyvault secret set --vault-name $KV --name premium-jwt-issuer --value "<issuer-value>"
az keyvault secret set --vault-name $KV --name premium-jwt-audience --value "<audience-value>"
```

### Verify

```powershell
az keyvault secret list --vault-name $KV -o table
```

## Step 2: Create the ACA Environment

If you already created the ACA environment during preparation, just verify it and move on.

### Portal path

1. Azure Portal -> search `Container Apps`
2. Click `Environments`
3. If the environment already exists, open it and confirm:
   - correct region
   - correct resource group
   - appropriate workload profile for your planned GPU worker
4. If it does not exist, click `Create`

### Verify

- [ ] ACA environment exists
- [ ] It is in the same region where GPU quota was confirmed
- [ ] It is the environment you will use for both apps

## Step 3: Deploy `babel-entitlements-api`

This app should stay simple:

- CPU only
- external ingress enabled
- user-assigned managed identity attached
- Key Vault-backed JWT config

### Portal path

1. Azure Portal -> `Container Apps`
2. Click `Create`
3. Set:
   - resource group: your existing RG
   - container app environment: your ACA environment
   - app name: `babel-entitlements-api`
4. On the container page:
   - image source: `Azure Container Registry`
   - choose the final entitlement API image
5. On identity:
   - attach the user-assigned managed identity
6. On ingress:
   - enable external ingress
   - set the target port to the final entitlement API port
7. On secrets / environment variables:
   - use Key Vault references for JWT settings
8. Create the app

### CLI skeleton

Do not run this until the entitlement API image name, port, and env vars are final.

```powershell
$RG="<resource-group>"
$ENV_NAME="<aca-environment-name>"
$APP_NAME="babel-entitlements-api"
$ACR_SERVER="<acr-login-server>"
$IMAGE="<entitlements-api-image>"
$MI_ID="<managed-identity-resource-id>"
$JWT_SIGNING_URI="<key-vault-secret-uri>"
$JWT_ISSUER_URI="<key-vault-secret-uri>"
$JWT_AUDIENCE_URI="<key-vault-secret-uri>"

az containerapp create `
  --resource-group $RG `
  --name $APP_NAME `
  --environment $ENV_NAME `
  --image "$ACR_SERVER/$IMAGE" `
  --user-assigned $MI_ID `
  --target-port <final-port> `
  --ingress external `
  --secrets `
    "premium-jwt-signing-key=keyvaultref:$JWT_SIGNING_URI,identityref:$MI_ID" `
    "premium-jwt-issuer=keyvaultref:$JWT_ISSUER_URI,identityref:$MI_ID" `
    "premium-jwt-audience=keyvaultref:$JWT_AUDIENCE_URI,identityref:$MI_ID"
```

### Post-create registry binding

If needed, set the app to pull from ACR through identity after creation.

```powershell
az containerapp registry set `
  --name $APP_NAME `
  --resource-group $RG `
  --server $ACR_SERVER `
  --identity $MI_ID
```

### Verify

- [ ] App deployed
- [ ] External ingress enabled
- [ ] Managed identity attached
- [ ] Health endpoint responds

## Step 4: Deploy `babel-qwen-worker`

This is the sensitive app. Do not deploy it with public ingress until the authenticated worker image exists.

### Worker expectations

- auth-enabled image only
- GPU-capable Container App
- managed identity attached
- ACR pull via managed identity
- Azure Files mount for model/cache persistence
- target port `8001`

### Portal path

1. Azure Portal -> `Container Apps`
2. Click `Create`
3. Set:
   - resource group: your existing RG
   - environment: your ACA environment
   - app name: `babel-qwen-worker`
4. On the container page:
   - image source: `Azure Container Registry`
   - choose the final authenticated worker image
   - target port: `8001`
5. On identity:
   - attach the user-assigned managed identity
6. On ingress:
   - enable ingress only after the auth-enabled image is ready
7. On volumes:
   - attach the Azure Files share
8. On environment variables:
   - set the final worker-specific values after repo implementation
9. Create the app

### CLI skeleton

Do not run this until the worker image, env vars, and mount paths are final.

```powershell
$RG="<resource-group>"
$ENV_NAME="<aca-environment-name>"
$APP_NAME="babel-qwen-worker"
$ACR_SERVER="<acr-login-server>"
$IMAGE="<worker-image>"
$MI_ID="<managed-identity-resource-id>"

az containerapp create `
  --resource-group $RG `
  --name $APP_NAME `
  --environment $ENV_NAME `
  --image "$ACR_SERVER/$IMAGE" `
  --user-assigned $MI_ID `
  --target-port 8001 `
  --ingress external
```

### Post-create registry binding

```powershell
az containerapp registry set `
  --name $APP_NAME `
  --resource-group $RG `
  --server $ACR_SERVER `
  --identity $MI_ID
```

### Verify

- [ ] App deployed
- [ ] Auth-enabled image used
- [ ] Managed identity attached
- [ ] Target port is `8001`
- [ ] Ingress is only enabled when auth is actually present

## Step 5: Mount Persistent Storage Into the Worker

The worker should not redownload and rebuild everything on every restart.

Persist at least:

- Hugging Face cache root
- model cache directory
- any output/temp directory that should survive restarts

### Portal path

1. Azure Portal -> `Container Apps`
2. Open `babel-qwen-worker`
3. Go to the volume/storage section in the current portal layout
4. Add an Azure Files-backed volume
5. Mount it into the worker container using the final cache path decided in the repo implementation

### Verify

- [ ] Volume exists
- [ ] Volume is mounted into the worker
- [ ] Mount path matches the final implementation

## Step 6: Health and Auth Validation

Validate in this order.

### Checklist

1. Entitlement API health responds
2. Worker health responds
3. Worker rejects unauthenticated requests
4. Worker accepts authenticated requests
5. Premium token expiration behaves correctly

### Suggested smoke-test sequence

- [ ] Hit entitlement API health endpoint
- [ ] Hit worker health endpoint
- [ ] Try a premium worker route without auth and confirm rejection
- [ ] Request a premium token from the entitlement API
- [ ] Retry the worker request with that token and confirm success

## Step 7: Desktop App Cutover

Once the premium flow works in Azure:

1. configure the desktop app to use the hosted premium worker
2. keep the premium/cloud option visibly distinct from the local GPU host option
3. do not silently replace the local provider with the cloud one

### Repo seam

The current external-host seam already exists in [AppSettings.cs](Services/Settings/AppSettings.cs).

## Step 8: End-to-End Premium Test

Run one real premium Qwen test from the desktop app and confirm all of this:

- [ ] premium user can access cloud Qwen
- [ ] non-premium user cannot
- [ ] worker auth is enforced
- [ ] audio generation succeeds
- [ ] the user-visible provider selection is clear

## What Is Still Placeholder Today

These items should be replaced once the repo-side implementation is finished:

- entitlement API image name
- entitlement API target port
- final entitlement API env vars
- final worker image tag
- final worker env vars
- final mounted cache paths
- exact health and auth test endpoints

## Recommended Order If You Are Doing This Live

1. Reopen the first doc and confirm Part 1 is fully done.
2. Finalize repo-side auth and entitlement implementation.
3. Fill in the placeholders in this doc.
4. Deploy entitlement API first.
5. Deploy worker second.
6. Validate auth before touching desktop app settings.
7. Only then test from the app.

## Reference Files in This Repo

- Main prep doc: [azure-premium-qwen-step-by-step.md](azure-premium-qwen-step-by-step.md)
- Worker safety note: [inference/main.py](inference/main.py)
- Worker image definition: [inference/Dockerfile](inference/Dockerfile)
- External host seam: [AppSettings.cs](Services/Settings/AppSettings.cs)
