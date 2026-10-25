# VPN Panel

App para Windows que deixa a VPN WireGuard fácil de usar: um clique para conectar, lista de servidores, escolha de quais apps passam pela VPN e aviso quando o servidor para de responder. Funciona com o cliente gratuito [WireSock](https://www.wiresock.net/) e com o arquivo `.conf` de qualquer provedor WireGuard (Proton VPN, Mullvad, Surfshark, IVPN...).

**[⬇ Baixar a versão mais recente](https://github.com/JMqz/VPN-Panel/releases/latest)**

---

## Guia de instalação

Você vai precisar de:

- Windows 10 ou 11;
- uma conta em um provedor de VPN que ofereça **WireGuard** (o plano gratuito da Proton VPN serve);
- uns 10 minutos.

### 1. Instale o VPN Panel

1. Abra a página de [Releases](https://github.com/JMqz/VPN-Panel/releases/latest) e baixe **`VPN-Panel-Setup-2.2.exe`**.
2. Abra o arquivo baixado.
3. Se aparecer a tela azul **"O Windows protegeu o computador"**, clique em **Mais informações** e depois em **Executar assim mesmo**. O aviso aparece porque o app não tem assinatura digital paga.
4. O Windows pede permissão de administrador: clique em **Sim**.
5. Escolha se quer o atalho na área de trabalho e clique em **Instalar**.
6. Clique em **Abrir o VPN Panel**.

O app fica em **Iniciar → VPN Panel**. Só na primeira vez que você abrir o app o Windows pede permissão. Depois disso, não pede mais.

### 2. Instale o WireSock

O WireSock é o programa gratuito que faz a conexão. O VPN Panel controla ele por você.

1. Na tela **"Vamos deixar tudo pronto"**, clique em **Baixar WireSock** (ou abra [wiresock.net](https://www.wiresock.net/)).
2. Baixe o **WireSock Secure Connect** e instale com as opções padrão.
3. Volte ao VPN Panel. O passo 1 fica marcado sozinho, sem precisar reabrir o app.

### 3. Baixe o arquivo do servidor (`.conf`)

Cada provedor gera um arquivo `.conf` com o servidor e a sua chave de acesso.

**Proton VPN**
1. Entre em [account.protonvpn.com](https://account.protonvpn.com/downloads).
2. Vá em **Downloads → Configurações do WireGuard**.
3. Escolha a plataforma **Windows** e um servidor (no plano grátis, os marcados como *Free*).
4. Clique em **Criar** e depois em **Baixar**.

**Mullvad**
1. Entre em [mullvad.net](https://mullvad.net/account/wireguard-config) com o número da sua conta.
2. Abra o **Gerador de configuração WireGuard** e escolha **Windows**.
3. Gere uma chave, escolha o país/cidade e baixe o arquivo.

**Outros provedores:** procure por "WireGuard" ou "configuração manual" no site ou no suporte do provedor.

> ⚠️ O arquivo `.conf` funciona como uma senha. Não compartilhe e não publique.

Dica: baixe arquivos de vários países e adicione todos. Assim dá para trocar de servidor com um clique.

### 4. Adicione o servidor no app

- Clique em **Escolher arquivo** (na primeira tela) ou em **Servidores → Adicionar**, **ou**
- arraste o arquivo `.conf` para a janela do VPN Panel.

O servidor aparece na lista com a sigla do país e a cidade.

### 5. Conecte

Na aba **Início**, clique no botão grande:

| O botão fica... | Significa |
|---|---|
| Cinza | Desconectado |
| Girando em âmbar | Conectando ao servidor |
| **Verde** | Conectado e protegido. O servidor respondeu. |
| Âmbar, "Sem resposta" | O servidor parou de responder. O app tenta de novo sozinho. |

Abaixo do botão aparece o que está protegido ("Todo o tráfego do PC está protegido") e há quanto tempo você está conectado. O quadro "Seu IP" mostra o endereço que os sites veem.

Pronto, já dá para usar. Os passos abaixo são opcionais.

### 6. Ajuste do seu jeito (opcional)

**Escolher quais apps usam a VPN** (aba **Apps**):
- **Todos:** o computador inteiro passa pela VPN. É o mais seguro e o padrão.
- **Só estes:** só os apps da lista usam a VPN; o resto usa sua internet normal. Por exemplo, só o navegador ou só o Discord.
- **Todos, exceto:** tudo passa pela VPN, menos os apps da lista. Por exemplo, um jogo que fica lento com VPN.

Adicione apps em **Apps abertos**, **Procurar programa** ou digitando o nome (ex.: `chrome`). As mudanças são salvas sozinhas. Se a VPN estiver ligada, clique em **Reconectar** para aplicar.

**Opções** (aba **Opções**):

| Opção | Para que serve |
|---|---|
| Reconectar se a conexão cair | Tenta voltar sozinho até 3 vezes (vem ligada) |
| Bloquear a internet se a VPN cair | *Kill switch*: nada sai do PC sem proteção enquanto a VPN estiver ligada |
| Iniciar com o Windows | O app abre escondido na bandeja, perto do relógio |
| Conectar ao abrir | Liga a VPN assim que o app abre. Junto com a opção anterior, você fica protegido desde que liga o PC. |
| Abrir sem pedir permissão | O Windows só pergunta na primeira vez (vem ligada) |

**Bandeja do Windows:** fechar a janela não fecha o app, ele continua perto do relógio. O ícone fica verde quando conectado, âmbar quando o servidor não responde e cinza quando desconectado. Clique com o botão direito para conectar, desconectar ou sair.

---

## Problemas comuns

| O que aparece | O que fazer |
|---|---|
| **"Sem resposta"** ou **"O servidor não respondeu"** | O servidor pode estar fora do ar ou a sua rede bloqueia VPN (comum em redes de empresa, escola ou Wi-Fi público). Troque de servidor em **Servidores**. Se nenhum funcionar, teste em outra rede. |
| **"O arquivo deste servidor tem uma chave inválida"** | O arquivo foi alterado ou a chave foi revogada no site do provedor. Baixe um arquivo novo. |
| **"WireSock não encontrado"** ou o passo 1 não marca | Instale o WireSock Secure Connect (passo 2). |
| **"O WireSock não conseguiu preparar a rede"** | Reinstale o WireSock e reinicie o computador. |
| O **IP mostrado continua o meu** estando conectado | Normal no modo **Só estes**: o app mostra o IP dos apps fora da VPN. No modo **Todos**, deve aparecer o IP do servidor. Clique no ↻ ao lado de "Seu IP" para verificar de novo. |
| **Fiquei sem internet** | Se o *kill switch* estiver ligado e o servidor parar de responder, a internet fica bloqueada. Clique em **Desconectar** ou troque de servidor. |
| O **antivírus** reclamou do instalador | Acontece com apps sem assinatura digital paga. Baixe sempre pela página de [Releases](https://github.com/JMqz/VPN-Panel/releases) deste repositório. |
| Quero ver o que aconteceu | **Opções → Ver log da conexão**. |

## Desinstalar

**Configurações → Aplicativos → Aplicativos instalados → VPN Panel → Desinstalar.**

A VPN é desconectada e o app é removido. Os seus servidores e configurações ficam guardados (para o caso de instalar de novo), a não ser que você ligue **"Apagar meus servidores e configurações"**. O WireSock é um programa separado e pode ser desinstalado do mesmo jeito.

## Sem instalar (versão portátil)

Na página de Releases também tem o **`VpnPanel-2.2-portatil.exe`**: é o mesmo app, sem instalador e sem atalho no menu Iniciar. Basta abrir.

---

## Para desenvolvedores

### Onde ficam as coisas

| Caminho | O que é |
|---|---|
| `src\` | Código: `App.cs`, `MainWindow.cs`, `Dialogs.cs`, `Setup.cs`, `MainWindow.xaml`, `Theme.xaml`, `app.manifest`, `setup.manifest`, `build.ps1` |
| `C:\Program Files\VPN Panel\VpnPanel.exe` | App instalado (pasta que só administradores alteram) |
| `%APPDATA%\VPN Panel\configs\*.conf` | Servidores importados (contêm chaves privadas) |
| `%APPDATA%\VPN Panel\settings.json` | Servidor ativo e opções |
| `%APPDATA%\VPN Panel\locations.json` | País/cidade de cada servidor (cache) |
| `%APPDATA%\VPN Panel\wiresock.log` | Saída do WireSock da última conexão |

### Compilar

```
powershell -NoProfile -ExecutionPolicy Bypass -File src\build.ps1
```

O build gera `dist\VpnPanel.exe` e `dist\VPN Panel Setup.exe` com o `csc.exe` que já vem no Windows (`C:\Windows\Microsoft.NET\Framework64\v4.0.30319`, .NET Framework 4.8), sem instalar nada. O compilador é C# 5, então não use sintaxe nova (`$"..."`, `?.`, membros com `=>`). O instalador usa o mesmo código, com o ponto de entrada `VpnPanel.SetupProgram`, o app embutido como recurso e um manifesto de administrador. O XAML e o ícone ficam embutidos e são carregados com `XamlReader`; os eventos são ligados no código pelo `x:Name`. Para atualizar uma cópia instalada, rode o setup novo ("Reinstalar"/"Atualizar").

### Como funciona

- **Permissão de administrador:** o exe é `asInvoker`.
  - Sem admin e sem argumentos, ele executa a tarefa agendada **VPN Panel - Abrir** (privilégio mais alto, via API COM do Agendador de Tarefas), se ela apontar para este exe.
  - Se não, ele se reabre com `runas` (UAC).
  - A cópia elevada cria ou atualiza a tarefa. Desligar "Abrir sem pedir permissão" remove a tarefa.
- **Iniciar com o Windows:** tarefa **VPN Panel** (no logon, + 10 s, `VpnPanel.exe --tray`).
- **Conectar:** `cmd.exe /c wiresock-client.exe run -config <arquivo> -log-level info [-network-lock enabled] > wiresock.log`. Assim o túnel continua se o app fechar. Desconectar encerra o processo.
- **Status:** "Conectado" exige o handshake do WireGuard. O app acompanha o `wiresock.log` procurando `Handshake initiation sent` / `Handshake response received`.
  - **15 s** sem resposta: mostra "Sem resposta".
  - **60 s** sem resposta: se a conexão já tinha funcionado, reconecta; se nunca funcionou, derruba o túnel e avisa. Assim o *kill switch* nunca deixa a internet bloqueada sem a pessoa saber.
  - Se o cliente fechar em menos de 10 s, conta como falha, e o aviso traduz o erro do log.
  - Túneis iniciados fora do app não têm log para conferir e aparecem como conectados.
- **Apps na VPN:** grava `AllowedApps` / `DisallowedApps` na seção `[Peer]` e mantém todas as outras linhas idênticas, byte a byte. Segundo relatos no fórum do WireSock, `DisallowedApps` falhou em algumas versões.
- **Instância única:** mutex `Global\VpnPanelWireSock`. Abrir o app de novo sinaliza `Global\VpnPanelWireSockShow`, e a janela aberta aparece.
- **Desinstalar:** `VpnPanel.exe --uninstall` remove as tarefas, os atalhos, a chave em `HKLM\...\Uninstall\VPN Panel` e a pasta em Program Files.

### Próximos passos

- Assinatura de código (sem o aviso do SmartScreen)
- Rodar como serviço do Windows, para conectar antes do login
